// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.Text.RegularExpressions
open System.Threading

open Microsoft.Extensions.Logging
open FSharp.Control.TaskBuilder

[<AutoOpen>]
module Logging =
    // Pre-compiled
    let private reqex =
        Regex(@"\{(.+?)(\[(.+?)\])?\}", RegexOptions.Multiline ||| RegexOptions.Compiled)

    let private loggerFormatRegex =
        Regex($@"{{{PlaceHolder.ResponseHeader}\[(.+?)\]}}", RegexOptions.Multiline ||| RegexOptions.Compiled)

    let mutable private placeholderCounter = 0

    let private replacer (_: Match) : string =
        $"{{{PlaceHolder.ResponseHeader}__{Interlocked.Increment(&placeholderCounter)}}}"

    let private lowerCaseHeaders (headers: Map<string, seq<string>>) : Map<string, seq<string>> =
        headers |> Seq.map (fun kv -> kv.Key.ToLowerInvariant(), kv.Value) |> Map.ofSeq

    let private getHeaderValue (headers: Map<string, seq<string>>) (key: string) : string =
        headers
        |> Map.tryFind key
        |> Option.defaultValue Seq.empty
        |> Seq.tryHead
        |> Option.defaultValue String.Empty

    let private log' logLevel ctx content =
        match ctx.Request.Logger with
        | Some logger ->
            let format = ctx.Request.LogFormat
            let request = ctx.Request
            let matches = reqex.Matches format
            let lowerCaseHeaders = lazy (lowerCaseHeaders ctx.Response.Headers)

            // Create an array with values in the same order as in the format string. Important to be lazy and not
            // stringify any values here. Only pass references to the objects themselves so the logger can stringify
            // when / if the values are actually being used / logged.
            let getValues _ =
                matches
                |> Seq.cast
                |> Seq.map (fun (match': Match) ->
                    match match'.Groups[1].Value with
                    | PlaceHolder.HttpMethod -> box request.Method
                    | PlaceHolder.RequestContent ->
                        ctx.Request.ContentBuilder
                        |> Option.map (fun builder -> builder ())
                        |> Option.toObj
                        :> _
                    | PlaceHolder.ResponseContent -> content :> _
                    | PlaceHolder.ResponseHeader ->
                        // GroupCollection returns empty string values for indexes beyond what was captured, therefore
                        // we don't cause an exception here if the optional second group was not captured
                        getHeaderValue lowerCaseHeaders.Value (match'.Groups[3].Value.ToLowerInvariant()) :> _
                    | key ->
                        // Look for the key in the extra info. This also enables custom HTTP handlers to add custom
                        // placeholders to the format string.
                        match ctx.Request.Items.TryFind key with
                        | Some value -> value :> _
                        | _ -> String.Empty :> _)
                |> Array.ofSeq

            let level, values = logLevel, getValues content

            let formatCompatibilityString = loggerFormatRegex.Replace(format, replacer)

            logger.Log(level, formatCompatibilityString, values)
        | _ -> ()

    /// Set the logger (ILogger) to use. Usually you would use `HttpContext.withLogger` instead to set the logger for
    /// all requests.
    let withLogger (logger: ILogger) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request =
                    { ctx.Request with
                        Logger = Some logger } })

    /// Set the log level to use (default is LogLevel.None).
    let withLogLevel (logLevel: LogLevel) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let mapper ctx =
            { ctx with
                Request = { ctx.Request with LogLevel = logLevel } }

        update mapper source

    /// Set the log message to use. Use in the pipeline somewhere before the `log` handler.
    let withLogMessage<'TSource> (msg: string) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let mapper ctx =
            { ctx with
                Request =
                    { ctx.Request with
                        Items = ctx.Request.Items.Add(PlaceHolder.Message, Value.String msg) } }

        update mapper source

    // Set the log format to use.
    let withLogFormat (format: string) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source
        |> update (fun ctx ->
            { ctx with
                Request = { ctx.Request with LogFormat = format } })

    /// Logger handler with message. Should be composed in pipeline after both the `fetch` handler, and the `withError`
    /// in order to log both requests, responses and errors.
    let log (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    task {
                        match ctx.Request.LogLevel with
                        | LogLevel.None -> ()
                        | logLevel -> log' logLevel ctx content

                        return! next.OnSuccessAsync(ctx, content)
                    }

                member _.OnErrorAsync(ctx, error) =
                    task {
                        match error with
                        | HttpException(ctx, error) ->
                            match ctx.Request.LogLevel with
                            | LogLevel.None -> ()
                            | _ -> log' LogLevel.Error ctx (Some error)

                            return! next.OnErrorAsync(ctx, error)
                        | err -> return! next.OnErrorAsync(ctx, err)
                    }

                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source
