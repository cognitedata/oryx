// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks
open FsToolkit.ErrorHandling

type IHttpNext<'TSource> =
    abstract member OnNextAsync : context: Context * ?content: 'TSource -> Task<unit>
    abstract member OnErrorAsync : context: Context * error: exn -> Task<unit>

type IHttpHandler<'TSource, 'TResult> =
    abstract member Subscribe : next: IHttpNext<'TResult> -> IHttpNext<'TSource>

type IHttpHandler<'TSource> = IHttpHandler<'TSource, 'TSource>

[<AutoOpen>]
module Handler =
    let result (tcs: TaskCompletionSource<'TResult option>) =
        { new IHttpNext<'TResult> with
            member __.OnNextAsync(_, ?response) = task { tcs.SetResult response }
            member __.OnErrorAsync(_, error) = task { tcs.SetException error } }

    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync (ctx: Context) (handler: IHttpHandler<'TSource, 'TResult>) : Task<Result<'TResult, exn>> =
        let tcs = TaskCompletionSource<'TResult option>()

        task {
            do! handler.Subscribe(result tcs).OnNextAsync(ctx)

            try
                match! tcs.Task with
                | Some value -> return Ok value
                | _ -> return OperationCanceledException() :> Exception |> Error
            with err -> return Error err
        }

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync (ctx: Context) (handler: IHttpHandler<'T, 'TResult>) : Task<'TResult> =
        task {
            let! result = runAsync ctx handler

            match result with
            | Ok value -> return value
            | Error err -> return raise err
        }

    /// Map the content of the HTTP handler.
    let map<'TSource, 'TResult> (mapper: 'TSource -> 'TResult) : IHttpHandler<'TSource, 'TResult> =
        { new IHttpHandler<'TSource, 'TResult> with
            member _.Subscribe(sink) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        match content with
                        | Some content -> sink.OnNextAsync(ctx, mapper content)
                        | None -> sink.OnNextAsync(ctx)

                    member _.OnErrorAsync(ctx, exn) = sink.OnErrorAsync(ctx, exn) } }

    /// Compose two HTTP handlers into one.
    let inline compose (first: IHttpHandler<'T1, 'T2>) (second: IHttpHandler<'T2, 'T3>) : IHttpHandler<'T1, 'T3> =
        { new IHttpHandler<'T1, 'T3> with
            member _.Subscribe(next) = second.Subscribe(next) |> first.Subscribe }

    /// Composes two HTTP handlers.
    let (>=>) = compose

    /// Thrown if no choice found.
    exception NoChoiceException of unit

    /// Choose from a list of handlers to use. The first handler that succeeds will be used.
    let choose (handlers: IHttpHandler<'TSource, 'TResult> seq) : IHttpHandler<'TSource, 'TResult> =
        { new IHttpHandler<'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        let mutable found = false

                        task {
                            let obv =
                                { new IHttpNext<'TResult> with
                                    member _.OnNextAsync(ctx, ?content) =
                                        found <- true
                                        next.OnNextAsync(ctx, ?content = content)

                                    member _.OnErrorAsync(ctx, exn) = Task.FromResult() }

                            for handler in handlers do
                                if not found then
                                    do!
                                        handler
                                            .Subscribe(obv)
                                            .OnNextAsync(ctx, ?content = content)

                            if not found then
                                return! next.OnErrorAsync(ctx, NoChoiceException())
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery (query: seq<struct (string * string)>) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request = { ctx.Request with Query = query } },
                            ?content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// HTTP handler for adding content builder to context. These
    /// content will be added to the HTTP body of requests that uses
    /// this context.
    let withContent<'TSource> (builder: unit -> HttpContent) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            ContentBuilder = Some builder } },
                            ?content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// HTTP handler for adding HTTP header to the context. The header
    /// will be added to the HTTP request when using the `fetch` HTTP
    /// handler.
    let withHeader<'TSource> (name: string) (value: string) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            Headers = ctx.Request.Headers.Add(name, value) } },
                            ?content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// HTTP handler for setting the expected response type.
    let withResponseType<'TSource> (respType: ResponseType) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            ResponseType = respType } },
                            ?content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// HTTP handler for setting the method to be used for requests
    /// using this context. You will normally want to use the `GET`,
    /// `POST`, `PUT`, `DELETE`, or `OPTIONS` HTTP handlers instead of
    /// this one.
    let withMethod<'TSource> (method: HttpMethod) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request = { ctx.Request with Method = method } },
                            ?content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// HTTP handler for building the URL.
    let withUrlBuilder<'TSource> (builder: UrlBuilder) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            UrlBuilder = builder } },
                            ?content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl<'TSource> (url: string) = withUrlBuilder<'TSource> (fun _ -> url)

    /// HTTP GET request. Also clears any content set in the context.
    let GET<'TSource> : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            Method = HttpMethod.Get
                                            ContentBuilder = None } },
                            ?content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// HTTP POST request.
    let POST<'TSource> = withMethod<'TSource> HttpMethod.Post
    /// HTTP PUT request.
    let PUT<'TSource> = withMethod<'TSource> HttpMethod.Put
    /// HTTP DELETE request.
    let DELETE<'TSource> = withMethod<'TSource> HttpMethod.Delete
    /// HTTP Options request.
    let OPTIONS<'TSource> = withMethod<'TSource> HttpMethod.Options

    /// Run list of HTTP handlers concurrently.
    let concurrent<'TSource, 'TResult>
        (handlers: seq<IHttpHandler<'TSource, 'TResult>>)
        : IHttpHandler<'TSource, 'TResult list> =
        { new IHttpHandler<'TSource, 'TResult list> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, _) =
                        task {
                            let res : Result<Context * 'TResult, exn> array = Array.zeroCreate (Seq.length handlers)

                            let obv n =
                                { new IHttpNext<'TResult> with
                                    member _.OnNextAsync(ctx, content) =
                                        task {
                                            match content with
                                            | Some content -> res.[n] <- Ok(ctx, content)
                                            | None -> res.[n] <- Error(ArgumentNullException() :> _)
                                        }

                                    member _.OnErrorAsync(_, err) = task { res.[n] <- Error err } }

                            let! _ =
                                handlers
                                |> Seq.mapi (fun n handler -> handler.Subscribe(obv n).OnNextAsync ctx)
                                |> Task.WhenAll

                            let result = res |> List.ofSeq |> List.sequenceResultM

                            match result with
                            | Ok results ->
                                let results, contents = results |> List.unzip
                                let bs = Context.merge results
                                return! next.OnNextAsync(bs, contents)
                            | Error err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// Run list of HTTP handlers sequentially.
    let sequential<'TSource, 'TResult>
        (handlers: seq<IHttpHandler<'TSource, 'TResult>>)
        : IHttpHandler<'TSource, 'TResult list> =
        { new IHttpHandler<'TSource, 'TResult list> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, _) =
                        task {
                            let res = ResizeArray<Result<Context * 'TResult, exn>>()

                            let obv =
                                { new IHttpNext<'TResult> with
                                    member _.OnNextAsync(ctx, content) =
                                        task {
                                            match content with
                                            | Some content -> Ok(ctx, content) |> res.Add
                                            | None -> Error(ArgumentNullException() :> exn) |> res.Add
                                        }

                                    member _.OnErrorAsync(_, err) = task { Error err |> res.Add } }

                            for handler in handlers do
                                do!
                                    handler.Subscribe(obv)
                                    |> (fun obv -> obv.OnNextAsync ctx)

                            let result = res |> List.ofSeq |> List.sequenceResultM

                            match result with
                            | Ok results ->
                                let results, contents = results |> List.unzip
                                let bs = Context.merge results
                                return! next.OnNextAsync(bs, contents)
                            | Error err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// Parse response stream to a user specified type synchronously.
    let parse<'TResult> (parser: Stream -> 'TResult) : IHttpHandler<HttpContent, 'TResult> =
        { new IHttpHandler<HttpContent, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            match content with
                            | None -> return! next.OnNextAsync(ctx)
                            | Some content ->
                                let! stream = content.ReadAsStreamAsync()

                                try
                                    let item = parser stream
                                    return! next.OnNextAsync(ctx, item)
                                with ex ->
                                    ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                                    return! next.OnErrorAsync(ctx, ex)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// Parse response stream to a user specified type asynchronously.
    let parseAsync<'TResult> (parser: Stream -> Task<'TResult>) : IHttpHandler<HttpContent, 'TResult> =
        { new IHttpHandler<HttpContent, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, ?content) =
                        task {
                            match content with
                            | None -> return! next.OnNextAsync(ctx)
                            | Some content ->
                                let! stream = content.ReadAsStreamAsync()

                                try
                                    let! item = parser stream
                                    return! next.OnNextAsync(ctx, item)
                                with ex ->
                                    ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                                    return! next.OnErrorAsync(ctx, ex)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// Use the given token provider to return a bearer token to use. This enables e.g. token refresh. The handler will
    /// fail the request if it's unable to authenticate.
    let withTokenRenewer<'TSource>
        (tokenProvider: CancellationToken -> Task<Result<string, exn>>)
        : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, _) =
                        task {
                            let! result =
                                task {
                                    try
                                        return! tokenProvider ctx.Request.CancellationToken
                                    with ex -> return Error ex
                                }

                            match result with
                            | Ok token ->
                                let ctx = Context.withBearerToken token ctx
                                return! next.OnNextAsync ctx
                            | Error err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    /// Use the given `completionMode` to change when the Response is considered to be 'complete'.
    ///
    /// Using `HttpCompletionOption.ResponseContentRead` (the default) means that the entire response content will be
    /// available in-memory when the handle response completes. This can lead to lower throughput in situations where
    /// files are being received over HTTP.
    ///
    /// In such cases, using `HttpCompletionOption.ResponseHeadersRead` can lead to faster response times overall, while
    /// not forcing the file stream to buffer in memory.
    let withCompletion<'TSource> (completionMode: HttpCompletionOption) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            CompletionMode = completionMode } },
                            ?content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }
