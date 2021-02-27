// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging
open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Net.Http

[<AutoOpen>]
module Logging =

    /// Set the logger (ILogger) to use. Usually you would use `Context.withLogger` instead to set the logger for all requests.
    let withLogger (logger: ILogger): HttpHandler<'TSource, 'TSource> =
        HttpHandler
        <| fun next ->
            { new IHttpNext<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                              Request =
                                  { ctx.Request with
                                        Logger = Some logger } },
                        ?content = content
                    )

                member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn) }

    /// Set the log level to use (default is LogLevel.None).
    let withLogLevel (logLevel: LogLevel): HttpHandler<'TSource, 'TSource> =
        HttpHandler
        <| fun next ->
            { new IHttpNext<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                              Request = { ctx.Request with LogLevel = logLevel } },
                        ?content = content
                    )

                member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn) }

    /// Set the log message to use. Use in the pipleline somewhere before the `log` handler.
    let withLogMessage<'TSource> (msg: string): HttpHandler<'TSource> =
        HttpHandler
        <| fun next ->
            { new IHttpNext<'TSource> with
                member _.NextAsync(ctx, ?content) =
                    next.NextAsync(
                        { ctx with
                              Request =
                                  { ctx.Request with
                                        Items = ctx.Request.Items.Add(PlaceHolder.Message, Value.String msg) } },
                        ?content = content
                    )

                member _.ErrorAsync(ctx, exn) = next.ErrorAsync(ctx, exn) }

    // Pre-compiled
    let private reqex =
        Regex(@"\{(.+?)\}", RegexOptions.Multiline ||| RegexOptions.Compiled)

    /// Logger handler with message. Should be composed in pipeline after the `fetch` handler, but before `withError` in
    /// order to log both requests, responses and errors.
    let log: HttpHandler<HttpContent, HttpContent> =
        HttpHandler
        <| fun next ->
            { new IHttpNext<HttpContent> with
                member _.NextAsync(ctx, ?content) =
                    task {
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
                                |> Seq.map
                                    (fun (matche: Match) ->
                                        match matche.Groups.[1].Value with
                                        | PlaceHolder.HttpMethod -> box request.Method
                                        | PlaceHolder.RequestContent ->
                                            ctx.Request.ContentBuilder
                                            |> Option.map (fun builder -> builder ())
                                            |> Option.toObj
                                            :> _
                                        | PlaceHolder.ResponseContent ->
                                            match response with
                                            | Some content -> content :> _
                                            | None -> null
                                        | key ->
                                            // Look for the key in the extra info. This also enables custom HTTP handlers to add custom
                                            // placeholders to the format string.
                                            match ctx.Request.Items.TryFind key with
                                            | Some value -> value :> _
                                            | _ -> String.Empty :> _)
                                |> Array.ofSeq

                            let level, values = request.LogLevel, getValues content
                            logger.Log(LogLevel.Error, format, values)
                        | _ -> ()

                        return! next.NextAsync(ctx, ?content = content)
                    }

                member _.ErrorAsync(ctx, exn) =
                    task {
                        //logger.Log(LogLevel.Error, format, values)
                        return! next.ErrorAsync(ctx, exn)
                    } }
