// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open FSharp.Control.Tasks.V2.ContextInsensitive

type RequestBuilder () =
    member _.Zero(): HttpHandler<'TSource, 'TSource> = id

    member _.Return(content: 'TSource): HttpHandler<'TSource> =
        fun next ->
            { new IHttpObserver<'TSource> with
                member _.NextAsync(ctx, _) = next.NextAsync(ctx, content = content)
                member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn)
            }

    member _.ReturnFrom(req: HttpHandler<'TSource, 'TResult>): HttpHandler<'TSource, 'TResult> = req

    member _.Delay(fn) = fn ()

    member _.For(source: 'T seq, func: 'T -> HttpHandler<'T, 'TNext>): HttpHandler<'T, 'TNext list> =
        let handlers = source |> Seq.map func
        handlers |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind
        (
            source: HttpHandler<'TSource, 'TNext>,
            fn: 'TNext -> HttpHandler<'TNext, 'TResult>
        ): HttpHandler<'TSource, 'TResult> =

        fun next ->
            { new IHttpObserver<'TNext> with
                member _.NextAsync(ctx, ?content) =
                    task {
                        let obv =
                            { new IHttpObserver<'TResult> with
                                member _.NextAsync(ctx', content) = next.NextAsync(ctx, ?content = content)
                                member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn)
                            }

                        match content with
                        | Some content ->
                            let res = fn content

                            return!
                                res obv
                                |> (fun obv -> obv.NextAsync(ctx, content = content))
                        | None -> return! obv.NextAsync(ctx)
                    }

                member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn)
            }
            |> source // Subscribe source

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder()
