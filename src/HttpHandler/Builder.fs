// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open Oryx.Middleware

type RequestBuilder () =
    member _.Zero() : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) = next }

    member _.Yield(content: 'TResult) : IHttpHandler<'TSource, 'TResult> = Core.singleton content
    member _.Return(content: 'TResult) : IHttpHandler<'TSource, 'TResult> = Core.singleton content
    member _.ReturnFrom(req: IHttpHandler<'TSource, 'TResult>) : IHttpHandler<'TSource, 'TResult> = req
    member _.Delay(fn) = fn ()
    member _.Combine(source, other) = source >=> other

    member _.For
        (
            source: 'TValue seq,
            func: 'TValue -> IHttpHandler<'TSource, 'TResult>
        ) : IHttpHandler<'TSource, 'TResult list> =
        source |> Seq.map func |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind
        (
            source: IHttpHandler<'TSource, 'TValue>,
            fn: 'TValue -> IHttpHandler<'TSource, 'TResult>
        ) : IHttpHandler<'TSource, 'TResult> =
        source |> Core.bind fn

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder()
