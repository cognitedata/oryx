// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

type HttpFuncResult = Task<unit>

type IHttpFunc<'TSource> =
    abstract member SendAsync: Context<'TSource> -> Task<unit>
    abstract member ThrowAsync: exn -> Task<unit>

///  abstract member Subscribe: IObserver<'TResult> -> IObserver<'TSource>
type HttpHandler<'TSource, 'TResult> = IHttpFunc<'TResult> -> IHttpFunc<'TSource>
type HttpHandler<'TSource> = IHttpFunc<'TSource> -> IHttpFunc<'TSource>

[<AutoOpen>]
module Handler =
    let finish (tcs: TaskCompletionSource<Context<'TSource>>) =
        { new IHttpFunc<'TSource> with
            member __.SendAsync x = task { tcs.SetResult x }
            member __.ThrowAsync exn = task { tcs.SetException exn }
        }

    /// Run the HTTP handler in the given context. Returns HttpResponse with headers and status-code etc.
    let runAsync'
        (ctx: Context<'TSource>)
        (handler: HttpHandler<'TSource, 'TResult>)
        : Task<Result<HttpResponse<'TResult>, exn>> =
        let tcs = TaskCompletionSource<Context<'TResult>>()

        let next = finish tcs

        task {
            do! (handler next).SendAsync ctx

            try
                let! result = tcs.Task
                return Ok result.Response
            with err -> return Error err
        }

    /// Run the HTTP handler in the given context. Returns content only.
    let runAsync (ctx: Context<'T>) (handler: HttpHandler<'T, 'TResult>): Task<Result<'TResult, exn>> =
        task {
            let! result = runAsync' ctx handler
            return result |> Result.map (fun a -> a.Content)
        }

    /// Map the content of the HTTP handler.
    let map<'TSource, 'TResult> (mapper: 'TSource -> 'TResult): HttpHandler<'TSource, 'TResult> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        {
                            Request = ctx.Request
                            Response = ctx.Response.Replace(mapper ctx.Response.Content)
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// Compose two HTTP handlers into one.
    let inline compose (first: HttpHandler<'T1, 'T2>) (second: HttpHandler<'T2, 'T3>): HttpHandler<'T1, 'T3> =
        first << second

    /// Composes two HTTP handlers.
    let (>=>) = compose

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery (query: seq<struct (string * string)>): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        { ctx with
                            Request = { ctx.Request with Query = query }
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// HTTP handler for adding content builder to context. These
    /// content will be added to the HTTP body of requests that uses
    /// this context.
    let withContent<'TSource> (builder: unit -> HttpContent): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        { ctx with
                            Request =
                                { ctx.Request with
                                    ContentBuilder = Some builder
                                }
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }


    /// HTTP handler for adding HTTP header to the context. The header
    /// will be added to the HTTP request when using the `fetch` HTTP
    /// handler.
    let withHeader<'TSource> (name: string) (value: string): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        { ctx with
                            Request =
                                { ctx.Request with
                                    Headers = ctx.Request.Headers.Add(name, value)
                                }
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// HTTP handler for setting the expected response type.
    let withResponseType<'TSource> (respType: ResponseType): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        { ctx with
                            Request =
                                { ctx.Request with
                                    ResponseType = respType
                                }
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// HTTP handler for setting the method to be used for requests
    /// using this context. You will normally want to use the `GET`,
    /// `POST`, `PUT`, `DELETE`, or `OPTIONS` HTTP handlers instead of
    /// this one.
    let withMethod<'TSource> (method: HttpMethod): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        { ctx with
                            Request = { ctx.Request with Method = method }
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// HTTP handler for building the URL.
    let withUrlBuilder<'TSource> (builder: UrlBuilder): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        { ctx with
                            Request =
                                { ctx.Request with
                                    UrlBuilder = builder
                                }
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl<'TSource> (url: string) = withUrlBuilder<'TSource> (fun _ -> url)

    /// HTTP GET request. Also clears any content set in the context.
    let GET<'TSource> : HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        { ctx with
                            Request =
                                { ctx.Request with
                                    Method = HttpMethod.Get
                                    ContentBuilder = None
                                }
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

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
        (handlers: seq<HttpHandler<'TSource, 'TResult>>)
        : HttpHandler<'TSource, 'TResult list> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    task {
                        let res =
                            ResizeArray<Result<Context<'TResult>, exn>>(Seq.length handlers)

                        let obv n =
                            { new IHttpFunc<'TResult> with
                                member _.SendAsync ctx = task { res.[n] <- Ok ctx }
                                member _.ThrowAsync err = task { res.[n] <- Error err }
                            }

                        let! _ =
                            handlers
                            |> Seq.mapi (fun n handler -> (handler (obv n)).SendAsync ctx)
                            |> Task.WhenAll

                        let result = res |> List.ofSeq |> Result.sequenceList

                        match result with
                        | Ok results ->
                            let bs = Context.mergeResponses results
                            return! next.SendAsync bs
                        | Error err -> return! next.ThrowAsync err
                    }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// Run list of HTTP handlers sequentially.
    let sequential<'TSource, 'TResult>
        (handlers: seq<HttpHandler<'TSource, 'TResult>>)
        : HttpHandler<'TSource, 'TResult list> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    task {
                        let res = ResizeArray<Result<Context<'TResult>, exn>>()

                        let obv =
                            { new IHttpFunc<'TResult> with
                                member _.SendAsync ctx = task { Ok ctx |> res.Add }
                                member _.ThrowAsync err = task { Error err |> res.Add }
                            }

                        for handler in handlers do
                            do! handler obv |> (fun h -> h.SendAsync ctx)

                        let result = res |> List.ofSeq |> Result.sequenceList

                        match result with
                        | Ok results ->
                            let bs = Context.mergeResponses results
                            return! next.SendAsync bs
                        | Error err -> return! next.ThrowAsync err
                    }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// Parse response stream to a user specified type synchronously.
    let parse<'TResult> (parser: Stream -> 'TResult): HttpHandler<HttpContent, 'TResult> =
        fun next ->
            { new IHttpFunc<HttpContent> with
                member _.SendAsync ctx =
                    task {
                        let! stream = ctx.Response.Content.ReadAsStreamAsync()

                        try
                            let item = parser stream

                            return!
                                next.SendAsync
                                    {
                                        Request = ctx.Request
                                        Response = ctx.Response.Replace(item)
                                    }
                        with ex ->
                            ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                            return! next.ThrowAsync ex
                    }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// Parse response stream to a user specified type asynchronously.
    let parseAsync<'TResult> (parser: Stream -> Task<'TResult>): HttpHandler<HttpContent, 'TResult> =
        fun next ->
            { new IHttpFunc<HttpContent> with
                member _.SendAsync ctx =
                    task {
                        let! stream = ctx.Response.Content.ReadAsStreamAsync()

                        try
                            let! item = parser stream

                            return!
                                next.SendAsync
                                    {
                                        Request = ctx.Request
                                        Response = ctx.Response.Replace(item)
                                    }
                        with ex ->
                            ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                            return! next.ThrowAsync ex
                    }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    /// Use the given token provider to return a bearer token to use. This enables e.g. token refresh. The handler will
    /// fail the request if it's unable to authenticate.
    let withTokenRenewer<'TSource>
        (tokenProvider: CancellationToken -> Task<Result<string, exn>>)
        : HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
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
                            return! next.SendAsync ctx
                        | Error err -> return! next.ThrowAsync err
                    }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }


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
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        { ctx with
                            Request =
                                { ctx.Request with
                                    CompletionMode = completionMode
                                }
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }
            |> source
