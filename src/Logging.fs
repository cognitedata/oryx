// Copyright 2020 Cognite AS

namespace Oryx

open System
open System.Net.Http
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging
open FSharp.Control.Tasks.V2.ContextInsensitive

[<AutoOpen>]
module Logging =

    let withLogger (logger: ILogger) (next: HttpFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with Logger = Some logger } }

    let withLogLevel (logLevel: LogLevel) (next: HttpFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with LogLevel = logLevel } }

    let withLogMessage (msg: string) (next: HttpFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with Items = context.Request.Items.Add (PlaceHolder.Message, Value.String msg)  } }

    // Pre-compiled
    let private reqex = Regex(@"\{(.+?)\}", RegexOptions.Multiline ||| RegexOptions.Compiled)

    [<Obsolete("Do not use. Use log / withLogMessage instead.")>]
    let logWithMessage (next: HttpFunc<'T, 'TResult, 'TError>) (ctx : Context<'T>) : HttpFuncResult<'TResult, 'TError> =
        next ctx

    /// Logger handler with message. Needs to be composed in the request before the fetch handler.
    let log (next: HttpFunc<'T, 'TResult, 'TError>) (ctx : Context<'T>) : HttpFuncResult<'TResult, 'TError> =
        task {
            let! result = next ctx

            match ctx.Request.Logger, ctx.Request.LogLevel with
            | _, LogLevel.None -> ()
            | Some logger, _ ->
                let format = ctx.Request.LogFormat
                let request = ctx.Request
                let matches = reqex.Matches format

                // Create an array with values in the same order as in the format string. Important to be lazy and not
                // stringify any values here. Only pass references to the objects themselves so the logger can stringify
                // when / if the values are acutally being used / logged.
                let getValues response =
                    matches
                    |> Seq.cast
                    |> Seq.map (fun (matche: Match) ->
                        match matche.Groups.[1].Value with
                        | PlaceHolder.HttpMethod -> box request.Method
                        | PlaceHolder.RequestContent ->
                            ctx.Request.ContentBuilder
                            |> Option.map (fun builder -> builder ())
                            |> Option.toObj :> _
                        | PlaceHolder.ResponseContent -> response :> _
                        | key ->
                            // Look for the key in the extra info. This also enables custom HTTP handlers to add custom
                            // placeholders to the format string.
                            match ctx.Request.Items.TryFind key with
                            | Some value -> value :> _
                            | _ -> String.Empty :> _
                    )
                    |> Array.ofSeq

                let level, values =
                    match result with
                    | Ok ctx' -> request.LogLevel, getValues ctx'.Response
                    | Error err -> LogLevel.Error, getValues err
                logger.Log (level, format, values)
            | _ -> ()
            return result
        }

