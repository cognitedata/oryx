// Copyright 2019 Cognite AS

namespace Oryx

open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks.V2.ContextInsensitive

[<AutoOpen>]
module Error =
    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch (errorHandler: HandlerError<'err> -> NextFunc<'a, 'r, 'err>) (next: HttpFunc<'a, 'r, 'err>) (ctx : Context<'a>) : HttpFuncResult<'r, 'err> = task {
        let! result = next ctx
        match result with
        | Ok ctx -> return Ok ctx
        | Error err -> return! errorHandler err ctx
    }

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError<'a, 'r, 'err> (errorHandler : HttpResponseMessage -> Task<HandlerError<'err>>) (next: NextFunc<HttpResponseMessage,'r, 'err>) (ctx: HttpContext) : HttpFuncResult<'r, 'err> =
        task {
            let response = ctx.Response
            match response.IsSuccessStatusCode with
            | true -> return! next ctx
            | false ->
                ctx.Request.Metrics.Counter Metric.FetchErrorInc Map.empty 1L

                let! err = errorHandler response
                return err |> Error
        }

