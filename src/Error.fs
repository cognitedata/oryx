// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks.V2.ContextInsensitive

[<AutoOpen>]
module Error =
    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch
        (errorHandler: HandlerError<'TError> -> HttpFunc<'T, 'TResult, 'TError>)
        (next: HttpFunc<'T, 'TResult, 'TError>)
        (ctx: Context<'T>)
        : HttpFuncResult<'TResult, 'TError>
        =
        task {
            let! result = next ctx

            match result with
            | Ok ctx -> return Ok ctx
            | Error err -> return! errorHandler err ctx
        }

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError<'T, 'TResult, 'TError>
        (errorHandler: HttpResponse<HttpContent> -> Task<HandlerError<'TError>>)
        (next: HttpFunc<HttpContent, 'TResult, 'TError>)
        (ctx: Context<HttpContent>)
        : HttpFuncResult<'TResult, 'TError>
        =
        task {
            let response = ctx.Response

            match response.IsSuccessStatusCode with
            | true -> return! next ctx
            | false ->
                ctx.Request.Metrics.Counter Metric.FetchErrorInc Map.empty 1L

                let! err = errorHandler response
                return err |> Error
        }
