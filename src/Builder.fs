// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

type RequestBuilder () =
    member _.Zero(): HttpHandler<unit, unit, _, 'err> = id

    member _.Return(res: 'T): HttpHandler<unit, 'T, _, 'TError> =
        fun next ctx ->
            next
                {
                    Request = ctx.Request
                    Response = ctx.Response.Replace(res)
                }

    member _.ReturnFrom(req: HttpHandler<'T, 'TNext, 'TResult, 'TError>): HttpHandler<'T, 'TNext, 'TResult, 'TError> =
        req

    member _.Delay(fn) = fn ()

    member _.For(source: 'T seq, func: 'T -> HttpHandler<'T, 'TNext, 'TNext, 'TError>)
                 : HttpHandler<'T, 'TNext list, 'TResult, 'TError> =
        source |> Seq.map func |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind(source: HttpHandler<'T, 'TValue, 'TResult, 'TError>,
                  fn: 'TValue -> HttpHandler<'T, 'TNext, 'TResult, 'TError>)
                  : HttpHandler<'T, 'TNext, 'TResult, 'TError> =

        fun next ctx ->
            let next' (ctx': Context<'TValue>) =
                fn
                    ctx'.Response.Content
                    next
                    { ctx with
                        // Preserve headers and status-code from previous response.
                        Response = ctx'.Response.Replace(ctx.Response.Content)
                    }

            source next' ctx // Run source is context

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder()
