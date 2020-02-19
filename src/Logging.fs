// Copyright 2020 Cognite AS

namespace Oryx

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging

[<AutoOpen>]
module Logging =

    let setLogger (logger: ILogger) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with Logger = Some logger } }

    let setLogLevel (logLevel: LogLevel) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with LogLevel = logLevel } }

    // Pre-compiled
    let private reqex = Regex(@"\{(.+?)\}", RegexOptions.Multiline)

    /// Logger handler with message. Needs to be composed in the request after the fetch handler.
    let logWithMsg (msg: string) (next: HttpFunc<'a, 'r, 'err>) (ctx : Context<'a>) : HttpFuncResult<'r, 'err> =
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
                    | "Method" -> box request.Method
                    | "Content" ->
                        ctx.Request.ContentBuilder
                        |> Option.map (fun builder -> builder ())
                        |> Option.toObj :> _
                    | "Url" ->
                        request.Extra.TryFind "Url"
                        |> Option.map box |> Option.toObj
                    | "Elapsed" ->
                        ctx.Request.Extra.TryFind "Elapsed" |> (fun opt ->
                            match opt with
                            | Some (Number value) -> box value
                            | _ -> null)
                    | "Response" -> ctx.Response :> _
                    | "Msg" -> msg :> _
                    | _ -> String.Empty :> _
                )
                |> Array.ofSeq
            logger.Log (request.LogLevel, format, valueArray)
        | _ -> ()
        next ctx

    /// Logger handler. Needs to be composed in the request after the fetch handler.
    let log (next: HttpFunc<'a, 'r, 'err>) (ctx : Context<'a>) : HttpFuncResult<'r, 'err> =
        logWithMsg String.Empty next ctx