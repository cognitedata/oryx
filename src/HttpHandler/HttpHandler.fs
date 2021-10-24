namespace Oryx

open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open Microsoft.Extensions.Logging

open FSharp.Control.Tasks
open Oryx.Middleware

type IHttpNext<'TSource> = IAsyncNext<HttpContext, 'TSource>
type IHttpHandler<'TResult> = IAsyncHandler<HttpContext, 'TResult>

[<AutoOpen>]
module HttpHandler =
    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TResult> (handler: IHttpHandler<'TResult>) =
        Core.runAsync<HttpContext, 'TResult> handler

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TResult> (handler: IHttpHandler<'TResult>) =
        Core.runUnsafeAsync<HttpContext, 'TResult> handler

    let singleton<'TSource> = Core.singleton<HttpContext, 'TSource>

    /// Map the content of the HTTP handler.
    let map<'TSource, 'TResult> = Core.map<HttpContext, 'TSource, 'TResult>
    let mapContext<'TSource> = Core.mapContext<HttpContext, 'TSource>

    /// Add HTTP header to context.
    let withHeader (header: string * string) =
        mapContext (fun ctx ->
            { ctx with
                  Request =
                      { ctx.Request with
                            Headers = ctx.Request.Headers.Add header } })

    /// Replace all headers in the context.
    let withHeaders (headers: Map<string, string>) =
        mapContext (fun ctx ->
            { ctx with
                  Request =
                      { ctx.Request with
                            Headers = headers } })

    /// Helper for setting Bearer token as Authorization header.
    let withBearerToken (token: string)  =
        let header = ("Authorization", $"Bearer {token}")
        withHeader header

    /// Set the HTTP client to use for the requests.
    let withHttpClient (client: HttpClient) =
         mapContext (fun ctx ->
            { ctx with
                  Request =
                      { ctx.Request with
                            HttpClient = (fun () -> client) } })

    /// Set the HTTP client factory to use for the requests.
    let withHttpClientFactory (factory: unit -> HttpClient) =
        mapContext (fun ctx ->
            { ctx with
                  Request =
                      { ctx.Request with
                            HttpClient = factory } })

    /// Set the URL builder to use.
    let withUrlBuilder (builder: HttpRequest -> string) =
         mapContext (fun ctx ->
              { ctx with
                  Request =
                      { ctx.Request with
                            UrlBuilder = builder } })

    /// Set a cancellation token to use for the requests.
    let withCancellationToken (token: CancellationToken) =
         mapContext (fun ctx ->
              { ctx with
                  Request =
                      { ctx.Request with
                            CancellationToken = token } })

    /// Set the logger (ILogger) to use.
    let withLogger (logger: ILogger) =
         mapContext (fun ctx ->
            { ctx with
                  Request =
                      { ctx.Request with
                            Logger = Some logger } })

    /// Set the log level to use (default is LogLevel.None).
    let withLogLevel (logLevel: LogLevel) =
        mapContext (fun ctx ->
            { ctx with
                  Request =
                      { ctx.Request with
                            LogLevel = logLevel } })

    /// Set the log format to use.
    let withLogFormat (format: string) =
        mapContext (fun ctx ->
            { ctx with
                  Request = { ctx.Request with LogFormat = format } })

    /// Set the log message to use (normally you would like to use the withLogMessage handler instead)
    let withLogMessage (msg: string) =
        mapContext (fun ctx ->
            { ctx with
                  Request =
                      { ctx.Request with
                            Items = ctx.Request.Items.Add(PlaceHolder.Message, Value.String msg) } })

    /// Set the metrics (IMetrics) to use.
    let withMetrics (metrics: IMetrics) =
        mapContext (fun ctx ->
            { ctx with
                  Request = { ctx.Request with Metrics = metrics } })

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery (query: seq<struct (string * string)>)
        (source: IHttpHandler<'TSource>) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Use(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request = { ctx.Request with Query = query } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }


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

    /// Handler for protecting the pipeline from exceptions and protocol violations.
    let protect<'TSource> = Error.protect<HttpContext, 'TSource>

    /// Choose from a list of handlers to use. The first handler that succeeds will be used.
    let choose<'TSource, 'TResult> = Error.choose<HttpContext, 'TSource, 'TResult>

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TSource> = Error.fail<HttpContext, 'TSource>

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TSource> = Error.panic<HttpContext, 'TSource>

    /// Validate content using a predicate function.
    let validate<'TSource> = Core.validate<HttpContext, 'TSource>

    /// Handler that skips (ignores) the content and outputs unit.
    let skip<'TSource> = Core.skip<HttpContext, 'TSource>

    /// Retrieves the content.
    let get<'TSource> () = map<'TSource, 'TSource> id

    /// Parse response stream to a user specified type synchronously.
    let parse<'TResult> (parser: Stream -> 'TResult) (source: IHttpHandler<HttpContent>) : IHttpHandler<'TResult> =
        { new IHttpHandler<'TResult> with
            member _.Use(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, content) =
                        unitVtask {
                            let! stream = content.ReadAsStreamAsync()

                            try
                                let item = parser stream
                                return! next.OnNextAsync(ctx, item)
                            with ex ->
                                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                                return! next.OnErrorAsync(ctx, ex)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }


    /// Parse response stream to a user specified type asynchronously.
    let parseAsync<'TResult> (parser: Stream -> Task<'TResult>) (source: IHttpHandler<HttpContent>) : IHttpHandler<'TResult> =
        { new IHttpHandler<'TResult> with
            member _.Use(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, content) =
                        unitVtask {
                            let! stream = content.ReadAsStreamAsync()

                            try
                                let! item = parser stream
                                return! next.OnNextAsync(ctx, item)
                            with ex ->
                                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                                return! next.OnErrorAsync(ctx, ex)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }

    /// HTTP handler for setting the expected response type.
    let withResponseType<'TSource> (respType: ResponseType) (source: IHttpHandler<'TSource>): IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Use(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            ResponseType = respType } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }

    /// HTTP handler for setting the method to be used for requests using this context. You will normally want to use
    /// the `GET`, `POST`, `PUT`, `DELETE`, or `OPTIONS` HTTP handlers instead of this one.
    let withMethod<'TSource> (method: HttpMethod) (source: IHttpHandler<'TSource>) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Use(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request = { ctx.Request with Method = method } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl (url: string) = withUrlBuilder (fun _ -> url)

    /// HTTP GET request. Also clears any content set in the context.
    let GET<'TSource> (source: IHttpHandler<'TSource>) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Use(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            Method = HttpMethod.Get
                                            ContentBuilder = None } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }

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
        (source: IHttpHandler<'TSource>)
        : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Use(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        unitVtask {
                            let! result =
                                task {
                                    try
                                        return! tokenProvider ctx.Request.CancellationToken
                                    with ex -> return Error ex
                                }

                            match result with
                            | Ok token ->
                                let name, value = ("Authorization", $"Bearer {token}")
                                return! next.OnNextAsync(
                                    { ctx with
                                          Request =
                                              { ctx.Request with
                                                    Headers = ctx.Request.Headers.Add(name, value) } }, content)
                            | Error err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }


    /// Use the given `completionMode` to change when the Response is considered to be 'complete'.
    ///
    /// Using `HttpCompletionOption.ResponseContentRead` (the default) means that the entire response content will be
    /// available in-memory when the handle response completes. This can lead to lower throughput in situations where
    /// files are being received over HTTP.
    ///
    /// In such cases, using `HttpCompletionOption.ResponseHeadersRead` can lead to faster response times overall, while
    /// not forcing the file stream to buffer in memory.
    let withCompletion<'TSource> (completionMode: HttpCompletionOption) (source: IHttpHandler<'TSource>) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Use(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            CompletionMode = completionMode } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }

    /// HTTP handler for adding content builder to context. These
    /// content will be added to the HTTP body of requests that uses
    /// this context.
    let withContent<'TSource> (builder: unit -> HttpContent) (source: IHttpHandler<'TSource>) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Use(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            ContentBuilder = Some builder } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } |> source.Use }

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError (errorHandler: HttpResponse -> HttpContent -> Task<exn>) (source: IHttpHandler<HttpContent>) : IHttpHandler<HttpContent> =
        { new IHttpHandler<HttpContent> with
            member _.Use(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, content) =
                        unitVtask {
                            let response = ctx.Response

                            match response.IsSuccessStatusCode with
                            | true -> return! next.OnNextAsync(ctx, content = content)
                            | false ->
                                ctx.Request.Metrics.Counter Metric.FetchErrorInc Map.empty 1L

                                let! err = errorHandler response content
                                return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, err) = next.OnErrorAsync(ctx, err) } |> source.Use }

    let empty<'TSource> = Core.empty<HttpContext> HttpContext.defaultContext
    let cache<'TSource> = Core.cache<HttpContext, 'TSource>
    let httpRequest = empty
