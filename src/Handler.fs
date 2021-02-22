// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

type HttpFuncResult = Task<unit>

type IHttpFunc<'TSource> =
    abstract member NextAsync: context: Context * ?content: 'TSource -> Task<unit>
    abstract member ThrowAsync: context: Context * error: exn -> Task<unit>

///  abstract member Subscribe: IObserver<'TResult> -> IObserver<'TSource>
type HttpHandler<'TSource, 'TResult> = IHttpFunc<'TResult> -> IHttpFunc<'TSource>
type HttpHandler<'TSource> = IHttpFunc<'TSource> -> IHttpFunc<'TSource>

[<AutoOpen>]
module Handler =
    let finish (tcs: TaskCompletionSource<'TResult option>) =
        { new IHttpFunc<'TResult> with
            member __.NextAsync(_, ?response) =
                task {
                    match response with
                    | Some response -> tcs.SetResult(Some response)
                    | None -> tcs.SetResult None
                }

            member __.ThrowAsync(_, error) = task { tcs.SetException error }
        }

    /// Run the HTTP handler in the given context. Returns HttpResponse with headers and status-code etc.
    let runAsync' (ctx: Context) (handler: HttpHandler<'TSource, 'TResult>): Task<Result<'TResult option, exn>> =
        let tcs = TaskCompletionSource<'TResult option>()

        let next = finish tcs

        task {
            do! (handler next).NextAsync(ctx)

            try
                let! result = tcs.Task
                return Ok result
            with err -> return Error err
        }

    /// Run the HTTP handler in the given context. Returns content only.
    let runAsync (ctx: Context) (handler: HttpHandler<'T, 'TResult>): Task<Result<'TResult option, exn>> =
        task {
            let! result = runAsync' ctx handler
            return result
        }

    let runUnsafeAsync (ctx: Context) (handler: HttpHandler<'T, 'TResult>): Task<'TResult> =
        task {
            let! result = runAsync' ctx handler

            match result with
            | Ok (Some result) -> return result
            | Ok (None) -> return raise <| OperationCanceledException()
            | Error err -> return raise err
        }

    /// Map the content of the HTTP handler.
    let map<'TSource, 'TResult> (mapper: 'TSource -> 'TResult): HttpHandler<'TSource, 'TResult> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    match content with
                    | Some content -> next.NextAsync(ctx, mapper content)
                    | None -> next.NextAsync(ctx)

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// Compose two HTTP handlers into one.
    let inline compose (first: HttpHandler<'T1, 'T2>) (second: HttpHandler<'T2, 'T3>): HttpHandler<'T1, 'T3> =
        second >> first

    /// Composes two HTTP handlers.
    let (>=>) = compose

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery (query: seq<struct (string * string)>): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                            Request = { ctx.Request with Query = query }
                        },
                        ?content = content
                    )

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// HTTP handler for adding content builder to context. These
    /// content will be added to the HTTP body of requests that uses
    /// this context.
    let withContent<'TSource> (builder: unit -> HttpContent): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                            Request =
                                { ctx.Request with
                                    ContentBuilder = Some builder
                                }
                        },
                        ?content = content
                    )

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }


    /// HTTP handler for adding HTTP header to the context. The header
    /// will be added to the HTTP request when using the `fetch` HTTP
    /// handler.
    let withHeader<'TSource> (name: string) (value: string): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                            Request =
                                { ctx.Request with
                                    Headers = ctx.Request.Headers.Add(name, value)
                                }
                        },
                        ?content = content
                    )

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// HTTP handler for setting the expected response type.
    let withResponseType<'TSource> (respType: ResponseType): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                            Request =
                                { ctx.Request with
                                    ResponseType = respType
                                }
                        },
                        ?content = content
                    )

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// HTTP handler for setting the method to be used for requests
    /// using this context. You will normally want to use the `GET`,
    /// `POST`, `PUT`, `DELETE`, or `OPTIONS` HTTP handlers instead of
    /// this one.
    let withMethod<'TSource> (method: HttpMethod): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, content) =
                    next.NextAsync(
                        { ctx with
                            Request = { ctx.Request with Method = method }
                        },
                        ?content = content
                    )

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// HTTP handler for building the URL.
    let withUrlBuilder<'TSource> (builder: UrlBuilder): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                            Request =
                                { ctx.Request with
                                    UrlBuilder = builder
                                }
                        },
                        ?content = content
                    )

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl<'TSource> (url: string) = withUrlBuilder<'TSource> (fun _ -> url)

    /// HTTP GET request. Also clears any content set in the context.
    let GET<'TSource> : HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                            Request =
                                { ctx.Request with
                                    Method = HttpMethod.Get
                                    ContentBuilder = None
                                }
                        },
                        ?content = content
                    )

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
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
                member _.NextAsync(ctx, _) =
                    task {
                        let res: Result<Context * 'TResult, exn> array = Array.zeroCreate (Seq.length handlers)

                        let obv n =
                            { new IHttpFunc<'TResult> with
                                member _.NextAsync(ctx, content) =
                                    task {
                                        match content with
                                        | Some content -> res.[n] <- Ok(ctx, content)
                                        | None -> res.[n] <- Error(ArgumentNullException() :> _)
                                    }

                                member _.ThrowAsync(_, err) = task { res.[n] <- Error err }
                            }

                        let! _ =
                            handlers
                            |> Seq.mapi (fun n handler -> (handler (obv n)).NextAsync ctx)
                            |> Task.WhenAll

                        let result = res |> List.ofSeq |> Result.sequenceList

                        match result with
                        | Ok results ->
                            let results, contents = results |> List.unzip
                            let bs = Context.mergeResponses results
                            return! next.NextAsync(bs, contents)
                        | Error err -> return! next.ThrowAsync(ctx, err)
                    }

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// Run list of HTTP handlers sequentially.
    let sequential<'TSource, 'TResult>
        (handlers: seq<HttpHandler<'TSource, 'TResult>>)
        : HttpHandler<'TSource, 'TResult list> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, _) =
                    task {
                        let res = ResizeArray<Result<Context * 'TResult, exn>>()

                        let obv =
                            { new IHttpFunc<'TResult> with
                                member _.NextAsync(ctx, content) =
                                    task {
                                        match content with
                                        | Some content -> Ok(ctx, content) |> res.Add
                                        | None -> Error(ArgumentNullException() :> exn) |> res.Add
                                    }

                                member _.ThrowAsync(_, err) = task { Error err |> res.Add }
                            }

                        for handler in handlers do
                            do! handler obv |> (fun obv -> obv.NextAsync ctx)

                        let result = res |> List.ofSeq |> Result.sequenceList

                        match result with
                        | Ok results ->
                            let results, contents = results |> List.unzip
                            let bs = Context.mergeResponses results
                            return! next.NextAsync(bs, contents)
                        | Error err -> return! next.ThrowAsync(ctx, err)
                    }

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// Parse response stream to a user specified type synchronously.
    let parse<'TResult> (parser: Stream -> 'TResult): HttpHandler<HttpContent, 'TResult> =
        fun next ->
            { new IHttpFunc<HttpContent> with
                member _.NextAsync(ctx, content) =
                    task {
                        match content with
                        | None -> return! next.NextAsync(ctx)
                        | Some content ->
                            let! stream = content.ReadAsStreamAsync()

                            try
                                let item = parser stream
                                return! next.NextAsync(ctx, item)
                            with ex ->
                                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                                return! next.ThrowAsync(ctx, ex)
                    }

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// Parse response stream to a user specified type asynchronously.
    let parseAsync<'TResult> (parser: Stream -> Task<'TResult>): HttpHandler<HttpContent, 'TResult> =
        fun next ->
            { new IHttpFunc<HttpContent> with
                member _.NextAsync(ctx, ?content) =
                    task {
                        match content with
                        | None -> return! next.NextAsync(ctx)
                        | Some content ->
                            let! stream = content.ReadAsStreamAsync()

                            try
                                let! item = parser stream
                                return! next.NextAsync(ctx, item)
                            with ex ->
                                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                                return! next.ThrowAsync(ctx, ex)
                    }

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }

    /// Use the given token provider to return a bearer token to use. This enables e.g. token refresh. The handler will
    /// fail the request if it's unable to authenticate.
    let withTokenRenewer<'TSource>
        (tokenProvider: CancellationToken -> Task<Result<string, exn>>)
        : HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.NextAsync(ctx, _) =
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
                            return! next.NextAsync ctx
                        | Error err -> return! next.ThrowAsync(ctx, err)
                    }

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
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
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                            Request =
                                { ctx.Request with
                                    CompletionMode = completionMode
                                }
                        },
                        ?content = content
                    )

                member _.ThrowAsync(ctx, exn) = next.ThrowAsync(ctx, exn)
            }
            |> source
