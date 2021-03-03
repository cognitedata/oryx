// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open Oryx.Middleware.Core

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

        source >=> bind fn

[<AutoOpen>]
module Builder =
    /// Content builder for an async context of request/result
    let middleware = MiddlewareBuilder()
