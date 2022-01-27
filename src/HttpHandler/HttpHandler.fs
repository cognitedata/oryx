namespace Oryx

open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.TaskBuilder
open Oryx
open Oryx.Middleware

type HttpHandler<'TResult> = Pipeline<HttpContext, 'TResult>

exception HttpException of (HttpContext * exn) with
    override this.ToString() =
        match this :> exn with
        | HttpException (_, err) -> err.ToString()
        | _ -> failwith "This should not never happen."

[<AutoOpen>]
module HttpHandler =
    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TResult> (handler: HttpHandler<'TResult>) =
        Core.runAsync<HttpContext, 'TResult> handler

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TResult> (handler: HttpHandler<'TResult>) =
        Core.runUnsafeAsync<HttpContext, 'TResult> handler

    /// Produce a single value using the default context.
    let singleton<'TSource> =
        Core.singleton<HttpContext, 'TSource> HttpContext.defaultContext

    /// Map the content of the HTTP handler.
    let map<'TSource, 'TResult> = Core.map<HttpContext, 'TSource, 'TResult>

    /// Update (map) the context.
    let update<'TSource> (update: HttpContext -> HttpContext) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        Core.update<HttpContext, 'TSource> update source

    /// Bind the content.
    let bind<'TSource, 'TResult> fn source =
        Core.bind<HttpContext, 'TSource, 'TResult> fn source

    /// Add HTTP header to context.
    let withHeader (header: string * string) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx -> { ctx with Request = { ctx.Request with Headers = ctx.Request.Headers.Add header } })

    /// Replace all headers in the context.
    let withHeaders (headers: Map<string, string>) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx -> { ctx with Request = { ctx.Request with Headers = headers } })

    /// Helper for setting Bearer token as Authorization header.
    let withBearerToken (token: string) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let header = ("Authorization", sprintf "Bearer %s" token)
        source |> withHeader header

    /// Set the HTTP client to use for the requests.
    let withHttpClient<'TSource> (client: HttpClient) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx -> { ctx with Request = { ctx.Request with HttpClient = (fun () -> client) } })

    /// Set the HTTP client factory to use for the requests.
    let withHttpClientFactory (factory: unit -> HttpClient) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx -> { ctx with Request = { ctx.Request with HttpClient = factory } })

    /// Set the URL builder to use.
    let withUrlBuilder (builder: HttpRequest -> string) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx -> { ctx with Request = { ctx.Request with UrlBuilder = builder } })

    /// Set a cancellation token to use for the requests.
    let withCancellationToken (token: CancellationToken) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx -> { ctx with Request = { ctx.Request with CancellationToken = token } })

    /// Set the metrics (IMetrics) to use.
    let withMetrics (metrics: IMetrics) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx -> { ctx with Request = { ctx.Request with Metrics = metrics } })

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery (query: seq<struct (string * string)>) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            fun ctx -> next { ctx with Request = { ctx.Request with Query = query } }
            |> source

    /// Chunks a sequence of HTTP handlers into sequential and concurrent batches.
    let chunk<'TSource, 'TNext, 'TResult> =
        Core.chunk<HttpContext, 'TSource, 'TNext, 'TResult> HttpContext.merge

    /// Run list of HTTP handlers sequentially.
    let sequential<'TSource, 'TResult> =
        Core.sequential<HttpContext, 'TSource, 'TResult> HttpContext.merge

    /// Run list of HTTP handlers concurrently.
    let concurrent<'TSource, 'TResult> =
        Core.concurrent<HttpContext, 'TSource, 'TResult> HttpContext.merge

    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TSource> = Error.catch<HttpContext, 'TSource>

    /// Choose from a list of handlers to use. The first handler that succeeds will be used.
    let choose<'TSource, 'TResult> = Error.choose<HttpContext, 'TSource, 'TResult>

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TSource, 'TResult> error source =
        Error.fail<HttpContext, 'TSource, 'TResult> error source

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TSource, 'TResult> error source =
        Error.panic<HttpContext, 'TSource, 'TResult> error source

    let ofError<'TSource> error =
        Error.ofError<HttpContext, 'TSource> HttpContext.defaultContext error
    /// Validate content using a predicate function.
    let validate<'TSource> = Core.validate<HttpContext, 'TSource>

    /// Handler that skips (ignores) the content and outputs unit.
    let ignoreContent<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<unit> =
        source
        |> Core.ignoreContent<HttpContext, 'TSource>

    let replace<'TSource, 'TResult> = Core.replace<HttpContext, 'TSource, 'TResult>

    /// Parse response stream to a user specified type synchronously.
    let parse<'TResult> (parser: Stream -> 'TResult) (source: HttpHandler<HttpContent>) : HttpHandler<'TResult> =
        fun onSuccess ->
            fun ctx (content: HttpContent) ->
                task {
                    let! stream = content.ReadAsStreamAsync()

                    try
                        let item = parser stream
                        return! onSuccess ctx item
                    with
                    | ex ->
                        ctx.Request.Metrics.Counter Metric.DecodeErrorInc ctx.Request.Labels 1L
                        raise ex
                }
            |> source

    /// Parse response stream to a user specified type asynchronously.
    let parseAsync<'TResult>
        (parser: Stream -> Task<'TResult>)
        (source: HttpHandler<HttpContent>)
        : HttpHandler<'TResult> =
        fun onSuccess ->
            fun ctx (content: HttpContent) ->
                task {
                    let! stream = content.ReadAsStreamAsync()

                    try
                        let! item = parser stream
                        return! onSuccess ctx item
                    with
                    | ex ->
                        ctx.Request.Metrics.Counter Metric.DecodeErrorInc ctx.Request.Labels 1L
                        raise ex
                }
            |> source

    /// HTTP handler for setting the expected response type.
    let withResponseType<'TSource> (respType: ResponseType) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun onSuccess ->
            fun ctx -> onSuccess { ctx with Request = { ctx.Request with ResponseType = respType } }
            |> source

    /// HTTP handler for setting the method to be used for requests using this context. You will normally want to use
    /// the `GET`, `POST`, `PUT`, `DELETE`, or `OPTIONS` HTTP handlers instead of this one.
    let withMethod<'TSource> (method: HttpMethod) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun onSuccess ->
            fun ctx -> onSuccess { ctx with Request = { ctx.Request with Method = method } }
            |> source

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl<'TSource> (url: string) source : HttpHandler<'TSource> = withUrlBuilder (fun _ -> url) source

    /// HTTP GET request. Also clears any content set in the context.
    let GET<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun onSuccess ->
            fun ctx ->
                onSuccess
                    { ctx with
                        Request =
                            { ctx.Request with
                                Method = HttpMethod.Get
                                ContentBuilder = None } }
            |> source

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
        fun onSuccess ->
            fun ctx content ->
                task {
                    let! result =
                        task {
                            try
                                return! tokenProvider ctx.Request.CancellationToken
                            with
                            | ex -> return Error ex
                        }

                    match result with
                    | Ok token ->
                        let name, value = ("Authorization", sprintf "Bearer %s" token)

                        return!
                            onSuccess
                                { ctx with Request = { ctx.Request with Headers = ctx.Request.Headers.Add(name, value) } }
                                content

                    | Error err -> raise err
                }
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
        fun next ->
            fun ctx -> next { ctx with Request = { ctx.Request with CompletionMode = completionMode } }
            |> source

    /// HTTP handler for adding content builder to context. These
    /// content will be added to the HTTP body of requests that uses
    /// this context.
    let withContent<'TSource> (builder: unit -> HttpContent) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun onSuccess ->
            fun ctx -> onSuccess { ctx with Request = { ctx.Request with ContentBuilder = Some builder } }
            |> source

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError
        (errorHandler: HttpResponse -> HttpContent -> Task<exn>)
        (source: HttpHandler<HttpContent>)
        : HttpHandler<HttpContent> =
        fun onSuccess onError onCancel ->
            fun ctx content ->
                task {
                    let response = ctx.Response

                    match response.IsSuccessStatusCode with
                    | true -> return! onSuccess ctx content
                    | false ->
                        ctx.Request.Metrics.Counter Metric.FetchErrorInc ctx.Request.Labels 1L

                        let! err = errorHandler response content
                        return! onError ctx (HttpException(ctx, err))
                }
            |> Core.swapArgs source onError onCancel

    /// Starts a pipeline using an empty request with the default context.
    let httpRequest: HttpHandler<unit> =
        Core.empty<HttpContext> HttpContext.defaultContext

    /// Caches the last content value and context.
    let cache<'TSource> = Core.cache<HttpContext, 'TSource>

    /// Asks for the given HTTP context and produces a content value using the context.
    let ask<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<HttpContext> =
        Core.ask<HttpContext, 'TSource> source
