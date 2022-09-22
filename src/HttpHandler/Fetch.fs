// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.Collections.Generic
open System.Diagnostics
open System.Net.Http
open System.Net.Http.Headers
open System.Web

open FSharp.Control.TaskBuilder
open Oryx.Pipeline

[<AutoOpen>]
module Fetch =
    type FormUrlEncodedContent with

        static member FromTuples values =
            let pairs = values |> Seq.map (fun (k, v) -> KeyValuePair<string, string>(k, v))

            let content = new FormUrlEncodedContent(pairs)
            content.Headers.ContentType <- MediaTypeHeaderValue "application/x-www-form-urlencoded"
            content :> HttpContent

    let buildRequest (client: HttpClient) (ctx: HttpContext) : HttpRequestMessage =
        let query = ctx.Request.Query

        let url =
            let result = ctx.Request.UrlBuilder ctx.Request

            if Seq.isEmpty query then
                result
            else
                let queryString = HttpUtility.ParseQueryString(String.Empty)

                for key, value in query do
                    queryString.Add(key, value)

                sprintf "%s?%s" result (queryString.ToString())

        let request = new HttpRequestMessage(ctx.Request.Method, url)

        let acceptHeader =
            match ctx.Request.ResponseType with
            | ResponseType.JsonValue -> "Accept", "application/json"
            | ResponseType.Protobuf -> "Accept", "application/protobuf"

        for KeyValue (header, value) in ctx.Request.Headers.Add(acceptHeader) do
            if not (client.DefaultRequestHeaders.Contains header) then
                request.Headers.Add(header, value)

        let content = ctx.Request.ContentBuilder |> Option.map (fun builder -> builder ())

        match content with
        | Some content -> request.Content <- content
        | _ -> ()

        request

    /// Fetch content using the given context. Exposes `{Url}`, `{ResponseContent}`, `{RequestContent}` and `{Elapsed}`
    /// to the log format.
    let fetch<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<HttpContent> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, _) =
                    task {
                        let timer = Stopwatch()
                        let client = ctx.Request.HttpClient()
                        let cancellationToken = ctx.Request.CancellationToken

                        try
                            use request = buildRequest client ctx
                            timer.Start()
                            ctx.Request.Metrics.Counter Metric.FetchInc ctx.Request.Labels 1L

                            let! response = client.SendAsync(request, ctx.Request.CompletionMode, cancellationToken)
                            timer.Stop()

                            ctx.Request.Metrics.Gauge
                                Metric.FetchLatencyUpdate
                                Map.empty
                                (float timer.ElapsedMilliseconds)

                            let items =
                                ctx
                                    .Request
                                    .Items
                                    .Add(PlaceHolder.Url, Value.Url request.RequestUri)
                                    .Add(PlaceHolder.Elapsed, Value.Number timer.ElapsedMilliseconds)

                            let headers = response.Headers |> Seq.map (|KeyValue|) |> Map.ofSeq

                            let! result =
                                next.OnSuccessAsync(
                                    { Request = { ctx.Request with Items = items }
                                      Response =
                                        { StatusCode = response.StatusCode
                                          IsSuccessStatusCode = response.IsSuccessStatusCode
                                          ReasonPhrase = response.ReasonPhrase
                                          Headers = headers } },
                                    response.Content
                                )


                            response.Dispose()
                            return result
                        with ex when not (ex :? HttpException) ->
                            return! next.OnErrorAsync(ctx, HttpException(ctx, ex))
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source
