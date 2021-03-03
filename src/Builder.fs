// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open FSharp.Control.Tasks

type RequestBuilder () =
    member _.Zero() : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) = next }

    member _.Return(content: 'TResult) : IHttpHandler<'TSource, 'TResult> =
        { new IHttpHandler<'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnNextAsync(ctx, content = content)
                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

    member _.ReturnFrom(req: IHttpHandler<'TSource, 'TResult>) : IHttpHandler<'TSource, 'TResult> = req

    member _.Delay(fn) = fn ()

    member _.For
        (
            source: 'TSource seq,
            func: 'TSource -> IHttpHandler<'TSource, 'TResult>
        ) : IHttpHandler<'TSource, 'TResult list> =
        let handlers = source |> Seq.map func
        handlers |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind
        (
            source: IHttpHandler<'TSource, 'TValue>,
            fn: 'TValue -> IHttpHandler<'TSource, 'TResult>
        ) : IHttpHandler<'TSource, 'TResult> =

        { new IHttpHandler<'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<'TValue> with
                    member _.OnNextAsync(ctx, ?content) =
                        task {
                            match content with
                            | Some content ->
                                let bound : IHttpHandler<'TSource, 'TResult> = fn content
                                return! bound.Subscribe(next).OnNextAsync(ctx)
                            | None -> return! next.OnNextAsync(ctx)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) }
                |> source.Subscribe }

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder()
