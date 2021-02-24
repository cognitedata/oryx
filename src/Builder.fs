// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open FSharp.Control.Tasks.V2.ContextInsensitive

type RequestBuilder () =
    member _.Zero(): HttpHandler<'TSource, 'TSource> = HttpHandler id

    member _.Return(content: 'TResult): HttpHandler<'TSource, 'TResult> =
        HttpHandler
        <| fun next ->
            { new IHttpNext<'TSource> with
                member _.NextAsync(ctx, _) = next.NextAsync(ctx, content = content)
                member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn)
            }

    member _.ReturnFrom(req: HttpHandler<'TSource, 'TResult>): HttpHandler<'TSource, 'TResult> = req

    member _.Delay(fn) = fn ()

    member _.For
        (
            source: 'TSource seq,
            func: 'TSource -> HttpHandler<'TSource, 'TResult>
        ): HttpHandler<'TSource, 'TResult list> =
        let handlers = source |> Seq.map func
        handlers |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind
        (
            source: HttpHandler<'TSource, 'TNext>,
            fn: 'TNext -> HttpHandler<'TNext, 'TResult>
        ): HttpHandler<'TSource, 'TResult> =

        let subscribe (next: IHttpNext<'TResult>) =
            let (next: IHttpNext<'TNext>) =
                { new IHttpNext<'TNext> with
                    member _.NextAsync(ctx, ?content) =
                        task {
                            let obv =
                                { new IHttpNext<'TResult> with
                                    member _.NextAsync(ctx', content) = next.NextAsync(ctx, ?content = content)
                                    member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn)
                                }

                            match content with
                            | Some content ->
                                let res = fn content

                                return!
                                    res.Subscribe(obv)
                                    |> (fun obv -> obv.NextAsync(ctx, content = content))
                            | None -> return! obv.NextAsync(ctx)
                        }

                    member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn)
                }

            source.Subscribe(next)

        HttpHandler subscribe

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder()
