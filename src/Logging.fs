// Copyright 2020 Cognite AS

namespace Oryx

open System
open System.Net.Http
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging
open FSharp.Control.Tasks.V2.ContextInsensitive

module PlaceHolder =
    [<Literal>]
    let HttpMethod = "HttpMethod"

    [<Literal>]
    let RequestContent = "RequestContent"

    [<Literal>]
    let ResponseContent = "ResponseContent"

    [<Literal>]
    let Message = "Message"

    [<Literal>]
    let Url = "Url"

    [<Literal>]
    let Elapsed = "Elapsed"

[<AutoOpen>]
module Logging =

    let withLogger (logger: ILogger) (next: HttpFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with Logger = Some logger } }

    let withLogLevel (logLevel: LogLevel) (next: HttpFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with LogLevel = logLevel } }

    // Pre-compiled
    let private reqex = Regex(@"\{(.+?)\}", RegexOptions.Multiline ||| RegexOptions.Compiled)

    /// Logger handler with message. Needs to be composed in the request after the fetch handler.
    let logWithMessage (msg: string) (next: HttpFunc<'T, 'TResult, 'TError>) (ctx : Context<'T>) : HttpFuncResult<'TResult, 'TError> =
        task {
            let! ctx' = next ctx
            let request = ctx.Request
            let format = ctx.Request.LogFormat                               
            match request.Logger, request.LogLevel with
            | _, LogLevel.None -> ()                
            | Some logger, _ ->
                match ctx' with
                | Ok res ->
                    printfn "%A" res.Response
                    let matches = reqex.Matches format
                    // Create an array with values in the same order as in the format string. Important to be lazy and not
                    // stringify any values here. Only pass references to the objects themselves so the logger can stringify
                    // when / if the values are acutally being used / logged.
                    let valueArray =
                        matches
                        |> Seq.cast
                        |> Seq.map (fun (matche: Match) ->
                            match matche.Groups.[1].Value with
                            | PlaceHolder.HttpMethod -> box request.Method
                            | PlaceHolder.RequestContent ->
                                ctx.Request.ContentBuilder
                                |> Option.map (fun builder -> builder ())
                                |> Option.toObj :> _
                            | PlaceHolder.ResponseContent -> res.Response :> _
                            | PlaceHolder.Message -> msg :> _
                            | key ->
                                // Look for the key in the extra info. This also enables custom HTTP handlers to add custom
                                // placeholders to the format string.
                                match ctx.Request.Items.TryFind key with
                                | Some value -> value :> _
                                | _ -> String.Empty :> _
                        )
                        |> Array.ofSeq
                    logger.Log (request.LogLevel, format, valueArray)
                | Error err ->
                    logger.Log (LogLevel.Error, format, err)
            | _ -> ()     

            return ctx'
        }

    /// Logger handler. Needs to be composed in the request after the fetch handler.
    let log (next: HttpFunc<'T, 'TResult, 'TError>) (ctx : Context<'T>) : HttpFuncResult<'TResult, 'TError> =
        logWithMessage String.Empty next ctx