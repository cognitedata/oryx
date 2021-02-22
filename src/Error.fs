// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks.V2.ContextInsensitive

[<AutoOpen>]
module Error =
    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch (errorHandler: exn -> HttpHandler<'TSource>): HttpHandler<'TSource> =
        fun next ->
            let rec obv =
                { new IHttpFunc<'TSource> with
                    member _.NextAsync(ctx, ?content) = next.NextAsync(ctx, ?content = content)

                    member _.ThrowAsync(ctx, err) =
                        task {
                            printfn "Got error"

                            let handler = errorHandler err next
                            return! handler.NextAsync(ctx)
                        }
                }

            obv

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError (errorHandler: HttpResponse -> HttpContent option -> Task<exn>): HttpHandler<HttpContent, HttpContent> =
        fun next ->
            { new IHttpFunc<HttpContent> with
                member _.NextAsync(ctx, ?content) =
                    task {
                        let response = ctx.Response

                        match response.IsSuccessStatusCode with
                        | true -> return! next.NextAsync(ctx, ?content=content)
                        | false ->
                            ctx.Request.Metrics.Counter Metric.FetchErrorInc Map.empty 1L

                            let! err = errorHandler response content
                            return! next.ThrowAsync(ctx, err)
                    }

                member _.ThrowAsync(ctx, err) = next.ThrowAsync(ctx, err)
            }
