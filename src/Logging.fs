// Copyright 2020 Cognite AS

namespace Oryx

open System
open System.Net.Http
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging

[<AutoOpen>]
module Logging =

    let withLogger (logger: ILogger) (next: NextFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with Logger = Some logger } }

    let withLogLevel (logLevel: LogLevel) (next: NextFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with LogLevel = logLevel } }

    // Pre-compiled
    let private reqex = Regex(@"\{(.+?)\}", RegexOptions.Multiline ||| RegexOptions.Compiled)

    /// Logger handler with message. Needs to be composed in the request after the fetch handler.
    let logWithMessage (msg: string) (next: HttpFunc<'T, 'TResult, 'TError>) (ctx : Context<'T>) : HttpFuncResult<'TResult, 'TError> =
        let request = ctx.Request

        match request.Logger, request.LogLevel with
        | _, LogLevel.None -> ()
        | Some logger, _ ->
            let format = ctx.Request.LogFormat
            let matches = reqex.Matches format
            // Create an array with values in the same order as in the format string. Important to be lazy and not
            // stringify any values here. Only pass references to the objects themselves so the logger can stringify
            // when / if the values are acutally being used / logged.
            let valueArray =
                matches
                |> Seq.cast
                |> Seq.map (fun (matche: Match) ->
                    match matche.Groups.[1].Value with
                    | "HttpMethod" -> box request.Method
                    | "RequestContent" ->
                        ctx.Request.ContentBuilder
                        |> Option.map (fun builder -> builder ())
                        |> Option.toObj :> _
                    | "ResponseContent" -> ctx.Response :> _
                    | "Message" -> msg :> _
                    | key ->
                        // Look for the key in the extra info. This also enables custom HTTP handlers to add custom
                        // placeholders to the format string.
                        match ctx.Request.Extra.TryFind key with
                        | Some value -> value :> _
                        | _ -> String.Empty :> _
                )
                |> Array.ofSeq
            logger.Log (request.LogLevel, format, valueArray)
        | _ -> ()
        next ctx

    /// Logger handler. Needs to be composed in the request after the fetch handler.
    let log (next: HttpFunc<'T, 'TResult, 'TError>) (ctx : Context<'T>) : HttpFuncResult<'TResult, 'TError> =
        logWithMessage String.Empty next ctx