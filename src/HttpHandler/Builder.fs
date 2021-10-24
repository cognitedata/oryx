// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open Oryx.Middleware

type RequestBuilder () =
    member _.Zero() : IHttpHandler<unit> = empty

    member _.Yield(content: 'TResult) : IHttpHandler<'TResult> = singleton content
    member _.Return(content: 'TResult) : IHttpHandler<'TResult> = singleton content
    member _.ReturnFrom(req: IHttpHandler<'TResult>) : IHttpHandler<'TResult> = req
    member _.Delay(fn) = fn ()
    member _.Combine(source, other) = [ source; other ] |> sequential

    member _.For
        (
            source: 'TSource seq,
            func: 'TSource -> IHttpHandler<'TResult>
        ) : IHttpHandler<'TResult list> =
        source
        |> Seq.map func
        |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind
        (
            source: IHttpHandler<'TSource>,
            fn: 'TSource -> IHttpHandler<'TResult>
        ) : IHttpHandler<'TResult> =
        source |> Core.bind fn

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let http = RequestBuilder()
