// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open Oryx.Middleware
open FSharp.Control.Tasks

type RequestBuilder () =
    member _.Zero() : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) = next }

    member _.Return(content: 'TResult) : IHttpHandler<'TSource, 'TResult> = Core.singleton content
    member _.ReturnFrom(req: IHttpHandler<'TSource, 'TResult>) : IHttpHandler<'TSource, 'TResult> = req
    member _.Delay(fn) = fn ()

    member _.For
        (
            source: 'TSource seq,
            func: 'TSource -> IHttpHandler<'TSource, 'TResult>
        ) : IHttpHandler<'TSource, 'TResult list> =
        source |> Seq.map func |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind
        (
            source: IHttpHandler<'TSource, 'TValue>,
            fn: 'TValue -> IHttpHandler<'TSource, 'TResult>
        ) : IHttpHandler<'TSource, 'TResult> =
        { new IHttpHandler<'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        let valueObs =
                            { new IHttpNext<'TValue> with
                                member _.OnNextAsync(ctx, value) =
                                    task {
                                        let bound : IHttpHandler<'TSource, 'TResult> = fn value
                                        return! bound.Subscribe(next).OnNextAsync(ctx, content)
                                    }

                                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                                member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) }

                        task {
                            let sourceObv = source.Subscribe(valueObs)
                            return! sourceObv.OnNextAsync(ctx, content)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder()
