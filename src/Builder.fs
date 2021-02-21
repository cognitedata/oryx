// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open FSharp.Control.Tasks.V2.ContextInsensitive

type RequestBuilder () =
    member _.Zero(): HttpHandler<unit, unit> = id

    member _.Return(res: 'TSource): HttpHandler<'TSource> =
        fun next ->
            { new IHttpFunc<'TSource> with
                member _.SendAsync ctx =
                    next.SendAsync
                        {
                            Request = ctx.Request
                            Response = ctx.Response.Replace(res)
                        }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }

    member _.ReturnFrom(req: HttpHandler<'T, 'TNext>): HttpHandler<'T, 'TNext> = req

    member _.Delay(fn) = fn ()

    // member _.For(source: 'T seq, func: 'T -> HttpHandler<'T, 'TNext>): HttpHandler<'T, 'TNext list> =
    //     let handlers = source |> Seq.map func
    //     handlers |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind
        (
            source: HttpHandler<'TSource, 'TValue>,
            fn: 'TValue -> HttpHandler<'TValue, 'TResult>
        ): HttpHandler<'TSource, 'TResult> =

        fun next ->
            { new IHttpFunc<'TValue> with
                member _.SendAsync ctx =
                    task {
                        let inner =
                            { new IHttpFunc<'TResult> with
                                member _.SendAsync ctx' =
                                    next.SendAsync
                                        { ctx' with
                                            // Preserve headers and status-code from previous response.
                                            Response = ctx.Response.Replace(ctx'.Response.Content)
                                        }

                                member _.ThrowAsync exn = next.ThrowAsync exn
                            }

                        let content = ctx.Response.Content
                        let res = fn content
                        do! res inner |> (fun h -> h.SendAsync ctx)
                    }

                member _.ThrowAsync exn = next.ThrowAsync exn
            }
            |> source


[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder()
