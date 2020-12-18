// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

type HandlerError<'TError> =
    /// Request failed with some exception, e.g HttpClient throws an exception, or JSON decode error.
    | Panic of exn
    /// User defined error response.
    | ResponseError of 'TError

    override this.ToString() =
        match this with
        | Panic exn -> exn.ToString()
        | ResponseError err -> err.ToString()

type HttpFuncResult<'TResult, 'TError> = Task<Result<Context<'TResult>, HandlerError<'TError>>>

type HttpFunc<'T, 'TResult, 'TError> = Context<'T> -> HttpFuncResult<'TResult, 'TError>

type HttpHandler<'T, 'TNext, 'TResult, 'TError> =
    HttpFunc<'TNext, 'TResult, 'TError> -> Context<'T> -> HttpFuncResult<'TResult, 'TError>

[<AutoOpen>]
module Handler =
    /// A next continuation that produces an Ok async result. Used to end the processing pipeline.
    let finishEarly<'T, 'TError> : HttpFunc<'T, 'T, 'TError> = Ok >> Task.FromResult

    /// Run the HTTP handler in the given context. Returns HttpResponse with headers and status-code etc.
    let runAsync'
        (ctx: Context<'T>)
        (handler: HttpHandler<'T, 'TResult, 'TResult, 'TError>)
        : Task<Result<HttpResponse<'TResult>, HandlerError<'TError>>>
        =
        task {
            let! result = (handler finishEarly) ctx
            return Result.map (fun a -> a.Response) result
        }

    /// Run the HTTP handler in the given context. Returns content only.
    let runAsync
        (ctx: Context<'T>)
        (handler: HttpHandler<'T, 'TResult, 'TResult, 'TError>)
        : Task<Result<'TResult, HandlerError<'TError>>>
        =
        task {
            let! result = (handler finishEarly) ctx
            return Result.map (fun a -> a.Response.Content) result
        }

    /// Map the content of the HTTP handler.
    let map
        (mapper: 'T1 -> 'T2)
        (next: HttpFunc<'T2, 'TResult, 'TError>)
        (ctx: Context<'T1>)
        : HttpFuncResult<'TResult, 'TError>
        =
        next
            {
                Request = ctx.Request
                Response = ctx.Response.Replace(mapper ctx.Response.Content)
            }

    /// Compose two HTTP handlers into one.
    let inline compose
        (first: HttpHandler<'T1, 'T2, 'TResult, 'TError>)
        (second: HttpHandler<'T2, 'T3, 'TResult, 'TError>)
        : HttpHandler<'T1, 'T3, 'TResult, 'TError>
        =
        second >> first

    /// Composes two HTTP handlers.
    let (>=>) = compose


    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery
        (query: seq<struct (string * string)>)
        (next: HttpFunc<unit, 'TResult, 'TError>)
        (context: HttpContext)
        =
        next
            { context with
                Request = { context.Request with Query = query }
            }

    /// HTTP handler for adding content builder to context. These
    /// content will be added to the HTTP body of requests that uses
    /// this context.
    let withContent<'TResult, 'TError>
        (builder: unit -> HttpContent)
        (next: HttpFunc<unit, 'TResult, 'TError>)
        (context: HttpContext)
        =
        next
            { context with
                Request =
                    { context.Request with
                        ContentBuilder = Some builder
                    }
            }

    /// HTTP handler for adding HTTP header to the context. The header
    /// will be added to the HTTP request when using the `fetch` HTTP
    /// handler.
    let withHeader<'TResult, 'TError>
        (name: string)
        (value: string)
        (next: HttpFunc<unit, 'TResult, 'TError>)
        (context: HttpContext)
        =
        next
            { context with
                Request =
                    { context.Request with
                        Headers = context.Request.Headers.Add(name, value)
                    }
            }


    /// HTTP handler for setting the expected response type.
    let withResponseType<'TResult, 'TError>
        (respType: ResponseType)
        (next: HttpFunc<unit, 'TResult, 'TError>)
        (context: HttpContext)
        =
        next
            { context with
                Request =
                    { context.Request with
                        ResponseType = respType
                    }
            }

    /// HTTP handler for setting the method to be used for requests
    /// using this context. You will normally want to use the `GET`,
    /// `POST`, `PUT`, `DELETE`, or `OPTIONS` HTTP handlers instead of
    /// this one.
    let withMethod<'TResult, 'TError>
        (method: HttpMethod)
        (next: HttpFunc<unit, 'TResult, 'TError>)
        (context: HttpContext)
        =
        next
            { context with
                Request = { context.Request with Method = method }
            }

    /// HTTP handler for building the URL.
    let withUrlBuilder<'TResult, 'TError>
        (builder: UrlBuilder)
        (next: HttpFunc<unit, 'TResult, 'TError>)
        (context: HttpContext)
        =
        next
            { context with
                Request =
                    { context.Request with
                        UrlBuilder = builder
                    }
            }

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl<'TResult, 'TError> (url: string) = withUrlBuilder<'TResult, 'TError> (fun _ -> url)

    /// HTTP GET request. Also clears any content set in the context.
    let GET<'TResult, 'TError> (next: HttpFunc<unit, 'TResult, 'TError>) (context: HttpContext) =
        next
            { context with
                Request =
                    { context.Request with
                        Method = HttpMethod.Get
                        ContentBuilder = None
                    }
            }

    /// HTTP POST request.
    let POST<'T, 'TError> = withMethod<'T, 'TError> HttpMethod.Post
    /// HTTP PUT request.
    let PUT<'T, 'TError> = withMethod<'T, 'TError> HttpMethod.Put
    /// HTTP DELETE request.
    let DELETE<'T, 'TError> = withMethod<'T, 'TError> HttpMethod.Delete
    /// HTTP Options request.
    let OPTIONS<'T, 'TError> = withMethod<'T, 'TError> HttpMethod.Options

    /// Run list of HTTP handlers concurrently.
    let concurrent<'T, 'TNext, 'TResult, 'TError>
        (handlers: seq<HttpHandler<'T, 'TNext, 'TNext, 'TError>>)
        (next: HttpFunc<'TNext list, 'TResult, 'TError>)
        (ctx: Context<'T>)
        : HttpFuncResult<'TResult, 'TError>
        =
        task {
            let! res =
                handlers
                |> Seq.map (fun handler -> handler finishEarly ctx)
                |> Task.WhenAll

            let result = res |> List.ofArray |> Result.sequenceList

            match result with
            | Ok results ->
                let bs =
                    {
                        Request = ctx.Request
                        Response = ctx.Response.Replace(results |> List.map (fun r -> r.Response.Content))
                    }

                return! next bs
            | Error err -> return Error err
        }

    /// Run list of HTTP handlers sequentially.
    let sequential<'T, 'TNext, 'TResult, 'TError>
        (handlers: seq<HttpHandler<'T, 'TNext, 'TNext, 'TError>>)
        (next: HttpFunc<'TNext list, 'TResult, 'TError>)
        (ctx: Context<'T>)
        : HttpFuncResult<'TResult, 'TError>
        =
        task {
            let res =
                ResizeArray<Result<Context<'TNext>, HandlerError<'TError>>>()

            for handler in handlers do
                let! result = handler finishEarly ctx
                res.Add result

            let result = res |> List.ofSeq |> Result.sequenceList

            match result with
            | Ok results ->
                let bs =
                    {
                        Request = ctx.Request
                        Response = ctx.Response.Replace(results |> List.map (fun c -> c.Response.Content))
                    }

                return! next bs
            | Error err -> return Error err
        }

    /// Parse response stream to a user specified type synchronously.
    let parse<'T, 'TResult, 'TError>
        (parser: Stream -> 'T)
        (next: HttpFunc<'T, 'TResult, 'TError>)
        (ctx: Context<HttpContent>)
        : HttpFuncResult<'TResult, 'TError>
        =
        task {
            let! stream = ctx.Response.Content.ReadAsStreamAsync()

            try
                let item = parser stream

                return!
                    next
                        {
                            Request = ctx.Request
                            Response = ctx.Response.Replace(item)
                        }
            with ex ->
                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                return Error(Panic ex)
        }

    /// Parse response stream to a user specified type asynchronously.
    let parseAsync<'T, 'TResult, 'TError>
        (parser: Stream -> Task<'T>)
        (next: HttpFunc<'T, 'TResult, 'TError>)
        (ctx: Context<HttpContent>)
        : HttpFuncResult<'TResult, 'TError>
        =
        task {
            let! stream = ctx.Response.Content.ReadAsStreamAsync()


            try
                let! item = parser stream

                return!
                    next
                        {
                            Request = ctx.Request
                            Response = ctx.Response.Replace(item)
                        }
            with ex ->
                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                return Error(Panic ex)
        }

    /// Extract header from response.
    let extractHeader<'T, 'TResult, 'TError> (header: string) (next: HttpFunc<_, _, 'TError>) (context: HttpContext) =
        task {
            let success, values = context.Response.Headers.TryGetValue header
            let values = if success then values else Seq.empty

            return!
                next
                    {
                        Request = context.Request
                        Response = context.Response.Replace(Ok values)
                    }
        }

    /// Use the given token provider to return a bearer token to use. This enables e.g. token refresh. The handler will
    /// fail the request if it's unable to authenticate.
    let withTokenRenewer<'TResult, 'TError>
        (tokenProvider: CancellationToken -> Task<Result<string, HandlerError<'TError>>>)
        (next: HttpFunc<unit, 'TResult, 'TError>)
        (ctx: HttpContext)
        =
        task {
            let! result =
                task {
                    try
                        return! tokenProvider ctx.Request.CancellationToken
                    with ex -> return Panic ex |> Error
                }

            match result with
            | Ok token ->
                let ctx = Context.withBearerToken token ctx
                return! next ctx
            | Error err -> return err |> Error
        }

    /// Use the given `completionMode` to change when the Response is considered to be 'complete'.
    ///
    /// Using `HttpCompletionOption.ResponseContentRead` (the default) means that the entire response content will be available in-memory when the handle response completes. This can lead to lower throughput in situations where files are being received over HTTP.
    ///
    /// In such cases, using `HttpCompletionOption.ResponseHeadersRead` can lead to faster response times overall, while not forcing the file stream to buffer in memory.
    let withCompletion<'TResult, 'TError>
        (completionMode: HttpCompletionOption)
        (next: HttpFunc<unit, 'TResult, 'TError>)
        (ctx: HttpContext)
        =
        next
            { ctx with
                Request =
                    { ctx.Request with
                        CompletionMode = completionMode
                    }
            }
