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
                    member _.SendAsync ctx = next.SendAsync ctx

                    member _.ThrowAsync err =
                        printfn "Got error"

                        task {
                            let next = errorHandler err
                            next obv |> (fun obv -> obv.SendAsync)
                        }
                }

            obv

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError (errorHandler: HttpResponse<HttpContent> -> Task<exn>): HttpHandler<HttpContent, HttpContent> =
        fun next ->
            { new IHttpFunc<HttpContent> with
                member _.SendAsync ctx =
                    task {
                        let response = ctx.Response

                        match response.IsSuccessStatusCode with
                        | true -> return! next.SendAsync ctx
                        | false ->
                            ctx.Request.Metrics.Counter Metric.FetchErrorInc Map.empty 1L

                            let! err = errorHandler response
                            return! next.ThrowAsync err
                    }

                member _.ThrowAsync err = next.ThrowAsync err
            }
