namespace Oryx

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks
open Oryx.Middleware

type IHttpNext<'TSource> = IAsyncNext<HttpContext, 'TSource>
type IHttpHandler<'TSource, 'TResult> = IAsyncMiddleware<HttpContext, 'TSource, 'TResult>
type IHttpHandler<'TSource> = IHttpHandler<'TSource, 'TSource>

[<AutoOpen>]
module HttpHandler =
    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TResult> (ctx: HttpContext) (handler: IHttpHandler<unit, 'TResult>) =
        Core.runAsync<HttpContext, 'TResult> ctx handler

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TResult> (ctx: HttpContext) (handler: IHttpHandler<unit, 'TResult>) =
        Core.runUnsafeAsync<HttpContext, 'TResult> ctx handler

    /// Compose two HTTP handlers into one.
    let inline compose (first: IHttpHandler<'T1, 'T2>) (second: IHttpHandler<'T2, 'T3>) : IHttpHandler<'T1, 'T3> =
        Core.compose first second

    /// Composes two HTTP handlers.
    let (>=>) = compose

    /// Return the given content.
    let singleton<'TSource, 'TResult> = Core.singleton<HttpContext, 'TSource, 'TResult>

    /// Map the content of the HTTP handler.
    let map<'TContext, 'TSource, 'TResult> = Core.map<HttpContext, 'TSource, 'TResult>

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery (query: seq<struct (string * string)>) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request = { ctx.Request with Query = query } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Chunks a sequence of HTTP handlers into sequential and concurrent batches.
    let chunk<'TSource, 'TNext, 'TResult> =
        Core.chunk<HttpContext, 'TSource, 'TNext, 'TResult> HttpContext.merge

    /// Run list of HTTP handlers sequentially.
    let sequential<'TContext, 'TSource, 'TResult> =
        Core.sequential<HttpContext, 'TSource, 'TResult> HttpContext.merge

    /// Run list of HTTP handlers concurrently.
    let concurrent<'TContext, 'TSource, 'TResult> =
        Core.concurrent<HttpContext, 'TSource, 'TResult> HttpContext.merge

    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TSource> = Error.catch<HttpContext, 'TSource>

    /// Choose from a list of handlers to use. The first handler that succeeds will be used.
    let choose<'TSource, 'TResult> = Error.choose<HttpContext, 'TSource, 'TResult>

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let throw<'TContext, 'TSource, 'TResult> = throw<HttpContext, 'TSource, 'TResult>

    /// Parse response stream to a user specified type synchronously.
    let parse<'TResult> (parser: Stream -> 'TResult) : IHttpHandler<HttpContent, 'TResult> =
        { new IHttpHandler<HttpContent, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            let! stream = content.ReadAsStreamAsync()

                            try
                                let item = parser stream
                                return! next.OnNextAsync(ctx, item)
                            with ex ->
                                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                                return! next.OnErrorAsync(ctx, ex)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }


    /// Parse response stream to a user specified type asynchronously.
    let parseAsync<'TResult> (parser: Stream -> Task<'TResult>) : IHttpHandler<HttpContent, 'TResult> =
        { new IHttpHandler<HttpContent, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            let! stream = content.ReadAsStreamAsync()

                            try
                                let! item = parser stream
                                return! next.OnNextAsync(ctx, item)
                            with ex ->
                                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                                return! next.OnErrorAsync(ctx, ex)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// HTTP handler for adding HTTP header to the context. The header
    /// will be added to the HTTP request when using the `fetch` HTTP
    /// handler.
    let withHeader<'TSource> (name: string) (value: string) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            Headers = ctx.Request.Headers.Add(name, value) } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// HTTP handler for setting the expected response type.
    let withResponseType<'TSource> (respType: ResponseType) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            ResponseType = respType } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// HTTP handler for setting the method to be used for requests using this context. You will normally want to use
    /// the `GET`, `POST`, `PUT`, `DELETE`, or `OPTIONS` HTTP handlers instead of this one.
    let withMethod<'TSource> (method: HttpMethod) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request = { ctx.Request with Method = method } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// HTTP handler for building the URL.
    let withUrlBuilder<'TSource> (builder: UrlBuilder) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            UrlBuilder = builder } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl<'TSource> (url: string) = withUrlBuilder<'TSource> (fun _ -> url)

    /// HTTP GET request. Also clears any content set in the context.
    let GET<'TSource> : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
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

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

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
        : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            let! result =
                                task {
                                    try
                                        return! tokenProvider ctx.Request.CancellationToken
                                    with ex -> return Error ex
                                }

                            match result with
                            | Ok token ->
                                let ctx = HttpContext.withBearerToken token ctx
                                return! next.OnNextAsync(ctx, content)
                            | Error err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }


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
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            CompletionMode = completionMode } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// HTTP handler for adding content builder to context. These
    /// content will be added to the HTTP body of requests that uses
    /// this context.
    let withContent<'TSource> (builder: unit -> HttpContent) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        next.OnNextAsync(
                            { ctx with
                                  Request =
                                      { ctx.Request with
                                            ContentBuilder = Some builder } },
                            content = content
                        )

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError (errorHandler: HttpResponse -> HttpContent -> Task<exn>) : IHttpHandler<HttpContent> =
        { new IHttpHandler<HttpContent> with
            member _.Subscribe(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            let response = ctx.Response

                            match response.IsSuccessStatusCode with
                            | true -> return! next.OnNextAsync(ctx, content = content)
                            | false ->
                                ctx.Request.Metrics.Counter Metric.FetchErrorInc Map.empty 1L

                                let! err = errorHandler response content
                                return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, err) = next.OnErrorAsync(ctx, err)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }
