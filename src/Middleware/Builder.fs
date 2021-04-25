// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open Oryx.Middleware.Core
open FSharp.Control.Tasks

type MiddlewareBuilder () =
    member _.Zero() : IAsyncMiddleware<'TContext, 'TSource> =
        { new IAsyncMiddleware<'TContext, 'TSource> with
            member _.Subscribe(next) = next }

    member _.Return(content: 'TResult) : IAsyncMiddleware<'TContext, 'TSource, 'TResult> = Core.singleton content

    member _.ReturnFrom
        (req: IAsyncMiddleware<'TContext, 'TSource, 'TResult>)
        : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        req

    member _.Delay(fn) = fn ()

    member _.For
        (
            source: 'TSource seq,
            func: 'TSource -> IAsyncMiddleware<'TContext, 'TSource, 'TResult>
        ) : IAsyncMiddleware<'TContext, 'TSource, 'TResult list> =
        source |> Seq.map func |> sequential List.head

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind
        (
            source: IAsyncMiddleware<'TContext, 'TSource, 'TValue>,
            fn: 'TValue -> IAsyncMiddleware<'TContext, 'TSource, 'TResult>
        ) : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        let valueObs =
                            { new IAsyncNext<'TContext, 'TValue> with
                                member _.OnNextAsync(ctx, value) =
                                    task {
                                        let bound : IAsyncMiddleware<'TContext, 'TSource, 'TResult> = fn value
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
    /// Content builder for an async context of request/result
    let middleware = MiddlewareBuilder()
