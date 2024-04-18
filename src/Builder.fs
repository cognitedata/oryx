// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx


type RequestBuilder() =
    member _.Zero() : HttpHandler<unit> = httpRequest

    member _.Yield(content: 'TResult) : HttpHandler<'TResult> = singleton content
    member _.Return(content: 'TResult) : HttpHandler<'TResult> = singleton content
    member _.ReturnFrom(req: HttpHandler<'TResult>) : HttpHandler<'TResult> = req
    member _.Delay(fn) = fn ()
    member _.Combine(source, other) = source |> bind (fun _ -> other)

    member _.For(source: 'TSource seq, func: 'TSource -> HttpHandler<'TResult>) : HttpHandler<'TResult list> =
        source |> Seq.map func |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind(source: HttpHandler<'TSource>, fn: 'TSource -> HttpHandler<'TResult>) : HttpHandler<'TResult> =
        source |> bind fn

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let http = RequestBuilder()
