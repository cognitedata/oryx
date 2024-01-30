// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0
namespace Oryx

open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.TaskBuilder
open FsToolkit.ErrorHandling

open Oryx

exception HttpException of (HttpContext * exn) with
    override this.ToString() =
        match this :> exn with
        | HttpException(_, err) -> err.ToString()
        | _ -> failwith "This should not never happen."

[<AutoOpen>]
module HttpHandler =
    /// Swap first with last arg so we can pipe onSuccess
    let swapArgs fn = fun a b c -> fn c a b

    /// A next continuation for observing the final result.
    let finish<'TResult> (tcs: TaskCompletionSource<'TResult>) : IHttpNext<'TResult> =

        { new IHttpNext<'TResult> with
            member x.OnSuccessAsync(_, response) = task { tcs.SetResult response }
            member x.OnErrorAsync(ctx, error) = task { tcs.SetException error }
            member x.OnCancelAsync(ctx) = task { tcs.SetCanceled() } }

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TResult> (handler: HttpHandler<'TResult>) : Task<'TResult> =
        let tcs = TaskCompletionSource<'TResult>()

        task {
            do! finish tcs |> handler
            return! tcs.Task
        }

    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TResult> (handler: HttpHandler<'TResult>) : Task<Result<'TResult, exn>> =
        task {
            try
                let! value = runUnsafeAsync handler
                return Ok value
            with error ->
                return Error error
        }

    /// Produce the given content.
    let singleton<'TSource> (content: 'TSource) : HttpHandler<'TSource> =
        fun next -> next.OnSuccessAsync(HttpContext.defaultContext, content)

    /// Map the content of the HTTP handler.
    let map<'TSource, 'TResult> (mapper: 'TSource -> 'TResult) (source: HttpHandler<'TSource>) : HttpHandler<'TResult> =

        fun next ->
            //fun ctx content -> success ctx (mapper content)
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    try
                        next.OnSuccessAsync(ctx, mapper content)
                    with error ->
                        next.OnErrorAsync(ctx, error)

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Bind the content of the HTTP handler..
    let bind<'TSource, 'TResult>
        (fn: 'TSource -> HttpHandler<'TResult>)
        (source: HttpHandler<'TSource>)
        : HttpHandler<'TResult> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    task {
                        let handler = fn content
                        return! handler next
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Run list of HTTP handlers concurrently.
    let concurrent<'TSource, 'TResult> (handlers: seq<HttpHandler<'TResult>>) : HttpHandler<'TResult list> =
        fun next ->
            task {
                let res: Result<HttpContext * 'TResult, HttpContext * exn> array =
                    Array.zeroCreate (Seq.length handlers)

                let obv n ctx content = task { res.[n] <- Ok(ctx, content) }

                let obv n =
                    { new IHttpNext<'TResult> with
                        member _.OnSuccessAsync(ctx, content) = task { res.[n] <- Ok(ctx, content) }
                        member _.OnErrorAsync(ctx, err) = task { res.[n] <- Error(ctx, err) }
                        member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }

                let tasks = handlers |> Seq.mapi (fun n handler -> handler (obv n))

                let! _ = Task.WhenAll(tasks)

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = HttpContext.merge results
                    return! next.OnSuccessAsync(bs, contents)
                | Error(_, err) -> raise err
            }

    /// Run list of HTTP handlers sequentially.
    let sequential<'TSource, 'TResult> (handlers: seq<HttpHandler<'TResult>>) : HttpHandler<'TResult list> =
        fun next ->
            task {
                let res = ResizeArray<Result<HttpContext * 'TResult, HttpContext * exn>>()

                let obv =
                    { new IHttpNext<'TResult> with
                        member _.OnSuccessAsync(ctx, content) = task { Ok(ctx, content) |> res.Add }
                        member _.OnErrorAsync(ctx, err) = task { res.Add(Error(ctx, err)) }
                        member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }

                for handler in handlers do
                    do! handler obv

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = HttpContext.merge results
                    return! next.OnSuccessAsync(bs, contents)
                | Error(_, err) -> raise err
            }

    /// Chunks a sequence of HTTP handlers into sequential and concurrent batches.
    let chunk<'TSource, 'TResult>
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TSource> -> HttpHandler<seq<'TResult>>)
        (items: seq<'TSource>)
        : HttpHandler<seq<'TResult>> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent)
        |> sequential
        // Collect results
        |> map (Seq.ofList >> Seq.collect (Seq.collect id))

    /// Handler that skips (ignores) the content and outputs unit.
    let ignoreContent<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<unit> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) = next.OnSuccessAsync(ctx, ())
                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Caches the last content value and context.
    let cache<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let mutable cache: (HttpContext * 'TSource) option = None

        fun next ->
            task {
                match cache with
                | Some(ctx, content) -> return! next.OnSuccessAsync(ctx, content)
                | _ ->
                    return!
                        { new IHttpNext<'TSource> with
                            member _.OnSuccessAsync(ctx, content) =
                                task {
                                    cache <- Some(ctx, content)
                                    return! next.OnSuccessAsync(ctx, content)
                                }

                            member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                            member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
                        |> source
            }

    /// Never produces a result.
    let never _ = task { () }

    /// Completes the current request.
    let empty (ctx: HttpContext) : HttpHandler<unit> =
        fun next -> next.OnSuccessAsync(ctx, ())

    /// Filter content using a predicate function.
    let filter<'TSource> (predicate: 'TSource -> bool) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, value) =
                    task {
                        if predicate value then
                            return! next.OnSuccessAsync(ctx, value)
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Validate content using a predicate function. Same as filter ut produces an error if validation fails.
    let validate<'TSource> (predicate: 'TSource -> bool) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, value) =
                    if predicate value then
                        next.OnSuccessAsync(ctx, value)
                    else
                        next.OnErrorAsync(ctx, SkipException "Validation failed")

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Retrieves the content.
    let await<'TSource> () (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source |> map<'TSource, 'TSource> id

    /// Asks for the given HTTP context and produces a content value using the context.
    let ask<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<HttpContext> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, _) = next.OnSuccessAsync(ctx, ctx)
                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Update (asks) the context.
    let update<'TSource> (update: HttpContext -> HttpContext) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    next.OnSuccessAsync(update ctx, content)

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Replaces the value with a constant.
    let replace<'TSource, 'TResult> (value: 'TResult) (source: HttpHandler<'TSource>) : HttpHandler<'TResult> =
        map (fun _ -> value) source

    /// Add HTTP header to context.
    let withHeader (header: string * string) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request =
                    { ctx.Request with
                        Headers = ctx.Request.Headers.Add header } })

    /// Replace all headers in the context.
    let withHeaders (headers: Map<string, string>) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request = { ctx.Request with Headers = headers } })

    /// Helper for setting Bearer token as Authorization header.
    let withBearerToken (token: string) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let header = ("Authorization", sprintf "Bearer %s" token)
        source |> withHeader header

    /// Set the HTTP client to use for the requests.
    let withHttpClient<'TSource> (client: HttpClient) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request =
                    { ctx.Request with
                        HttpClient = (fun () -> client) } })

    /// Set the HTTP client factory to use for the requests.
    let withHttpClientFactory (factory: unit -> HttpClient) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request =
                    { ctx.Request with
                        HttpClient = factory } })

    /// Set the URL builder to use.
    let withUrlBuilder (builder: HttpRequest -> string) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request =
                    { ctx.Request with
                        UrlBuilder = builder } })

    /// Set a cancellation token to use for the requests.
    let withCancellationToken (token: CancellationToken) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request =
                    { ctx.Request with
                        CancellationToken = token } })

    /// Set the metrics (IMetrics) to use.
    let withMetrics (metrics: IMetrics) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request = { ctx.Request with Metrics = metrics } })

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery (query: seq<struct (string * string)>) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    let ctx' =
                        { ctx with
                            Request = { ctx.Request with Query = query } }

                    next.OnSuccessAsync(ctx', content)

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TSource> = Error.catch<'TSource>

    /// Choose from a list of handlers to use. The first handler that succeeds will be used.
    let choose<'TSource, 'TResult> = Error.choose<'TSource, 'TResult>

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TSource, 'TResult> error source =
        Error.fail<'TSource, 'TResult> error source

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TSource, 'TResult> error source =
        Error.panic<'TSource, 'TResult> error source

    /// Parse response stream to a user specified type synchronously.
    let parse<'TResult> (parser: Stream -> 'TResult) (source: HttpHandler<HttpContent>) : HttpHandler<'TResult> =
        fun next ->
            { new IHttpNext<HttpContent> with
                member _.OnSuccessAsync(ctx, content: HttpContent) =
                    task {
                        let! stream = content.ReadAsStreamAsync()

                        try
                            let item = parser stream
                            return! next.OnSuccessAsync(ctx, item)
                        with ex ->
                            ctx.Request.Metrics.Counter Metric.DecodeErrorInc ctx.Request.Labels 1L
                            return! next.OnErrorAsync(ctx, ex)
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Parse response stream to a user specified type asynchronously.
    let parseAsync<'TResult>
        (parser: Stream -> Task<'TResult>)
        (source: HttpHandler<HttpContent>)
        : HttpHandler<'TResult> =
        fun next ->
            { new IHttpNext<HttpContent> with
                member _.OnSuccessAsync(ctx, content: HttpContent) =
                    task {
                        let! stream = content.ReadAsStreamAsync()

                        try
                            let! item = parser stream
                            return! next.OnSuccessAsync(ctx, item)
                        with ex ->
                            ctx.Request.Metrics.Counter Metric.DecodeErrorInc ctx.Request.Labels 1L
                            return! next.OnErrorAsync(ctx, ex)
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// HTTP handler for setting the expected response type.
    let withResponseType<'TSource> (respType: ResponseType) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    let ctx' =
                        { ctx with
                            Request =
                                { ctx.Request with
                                    ResponseType = respType } }

                    next.OnSuccessAsync(ctx', content)

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// HTTP handler for setting the method to be used for requests using this context. You will normally want to use
    /// the `GET`, `POST`, `PUT`, `DELETE`, or `OPTIONS` HTTP handlers instead of this one.
    let withMethod<'TSource> (method: HttpMethod) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let mapper ctx =
            { ctx with
                Request = { ctx.Request with Method = method } }

        update mapper source


    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl<'TSource> (url: string) source : HttpHandler<'TSource> = withUrlBuilder (fun _ -> url) source

    /// HTTP GET request. Also clears any content set in the context.
    let GET<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let mapper ctx =
            { ctx with
                Request =
                    { ctx.Request with
                        Method = HttpMethod.Get
                        ContentBuilder = None } }

        update mapper source

    /// HTTP POST request.
    let POST<'TSource> = withMethod<'TSource> HttpMethod.Post
    /// HTTP PUT request.
    let PUT<'TSource> = withMethod<'TSource> HttpMethod.Put
    /// HTTP DELETE request.
    let DELETE<'TSource> = withMethod<'TSource> HttpMethod.Delete
    /// HTTP Options request.
    let OPTIONS<'TSource> = withMethod<'TSource> HttpMethod.Options

    /// Use the given token provider to return a bearer token to use. This enables e.g. token refresh. The handler will
    /// fail the request if it's unable to authenticate.
    let withTokenRenewer<'TSource>
        (tokenProvider: CancellationToken -> Task<Result<string, exn>>)
        (source: HttpHandler<'TSource>)
        : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    task {
                        let! result =
                            task {
                                try
                                    return! tokenProvider ctx.Request.CancellationToken
                                with ex ->
                                    return Error ex
                            }

                        match result with
                        | Ok token ->
                            let name, value = ("Authorization", sprintf "Bearer %s" token)

                            return!
                                next.OnSuccessAsync(
                                    { ctx with
                                        Request =
                                            { ctx.Request with
                                                Headers = ctx.Request.Headers.Add(name, value) } },
                                    content
                                )

                        | Error err -> raise err
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Use the given `completionMode` to change when the Response is considered to be 'complete'.
    ///
    /// Using `HttpCompletionOption.ResponseContentRead` (the default) means that the entire response content will be
    /// available in-memory when the handle response completes. This can lead to lower throughput in situations where
    /// files are being received over HTTP.
    ///
    /// In such cases, using `HttpCompletionOption.ResponseHeadersRead` can lead to faster response times overall, while
    /// not forcing the file stream to buffer in memory.
    let withCompletion<'TSource>
        (completionMode: HttpCompletionOption)
        (source: HttpHandler<'TSource>)
        : HttpHandler<'TSource> =

        let mapper ctx =
            { ctx with
                Request =
                    { ctx.Request with
                        CompletionMode = completionMode } }

        update mapper source

    /// HTTP handler for adding content builder to context. These
    /// content will be added to the HTTP body of requests that uses
    /// this context.
    let withContent<'TSource> (builder: unit -> HttpContent) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let mapper ctx =
            { ctx with
                Request =
                    { ctx.Request with
                        ContentBuilder = Some builder } }

        update mapper source

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError
        (errorHandler: HttpResponse -> HttpContent -> Task<exn>)
        (source: HttpHandler<HttpContent>)
        : HttpHandler<HttpContent> =
        fun next ->
            { new IHttpNext<HttpContent> with
                member _.OnSuccessAsync(ctx, content) =
                    task {
                        let response = ctx.Response

                        match response.IsSuccessStatusCode with
                        | true -> return! next.OnSuccessAsync(ctx, content)
                        | false ->
                            ctx.Request.Metrics.Counter Metric.FetchErrorInc ctx.Request.Labels 1L

                            let! err = errorHandler response content
                            return! next.OnErrorAsync(ctx, HttpException(ctx, err))
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Starts a pipeline using an empty request with the default context.
    let httpRequest: HttpHandler<unit> = empty HttpContext.defaultContext
