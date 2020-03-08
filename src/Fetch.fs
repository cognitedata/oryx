// Copyright 2019 Cognite AS

namespace Oryx

open System
open System.Collections.Generic
open System.Diagnostics
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Web

open FSharp.Control.Tasks.V2.ContextInsensitive

[<AutoOpen>]
module Fetch =
    type FormUrlEncodedContent with
        static member FromTuples values =
            let pairs = values |> Seq.map (fun (k, v) -> new KeyValuePair<string, string>(k, v))
            let content = new FormUrlEncodedContent (pairs)
            content.Headers.ContentType <- MediaTypeHeaderValue "application/x-www-form-urlencoded"
            content :> HttpContent

    let buildRequest (client: HttpClient) (ctx: HttpContext) : HttpRequestMessage =
        let query = ctx.Request.Query
        let url =
            let result = ctx.Request.UrlBuilder ctx.Request
            if Seq.isEmpty query
            then result
            else
                let queryString = HttpUtility.ParseQueryString(String.Empty)
                for (key, value) in query do
                    queryString.Add (key, value)
                sprintf "%s?%s" result (queryString.ToString ())

        let request = new HttpRequestMessage (ctx.Request.Method, url)

        let acceptHeader =
            match ctx.Request.ResponseType with
            | JsonValue -> "Accept", "application/json"
            | Protobuf -> "Accept", "application/protobuf"

        for KeyValue(header, value) in ctx.Request.Headers.Add(acceptHeader) do
            if not (client.DefaultRequestHeaders.Contains header) then
                request.Headers.Add (header, value)

        let content = ctx.Request.ContentBuilder |> Option.map (fun builder -> builder ())
        match content with
        | Some content -> request.Content <- content
        | _ -> ()
        request

    /// Fetch content using the given context. Exposes `{Url}`, `{ResponseContent}`, `{RequestContent}` and `{Elapsed}`
    /// to the log format.
    let fetch<'T, 'TError> (next: HttpFunc<HttpResponseMessage, 'T, 'TError>) (ctx: HttpContext) : HttpFuncResult<'T, 'TError> =
        let timer = Stopwatch ()
        let client = ctx.Request.HttpClient ()

        let cancellationToken = ctx.Request.CancellationToken

        task {
            try
                use request = buildRequest client ctx
                timer.Start ()
                // Note: we don't use `use!` for response since the next handler will never throw exceptions. Thus we
                // can dispose ourselves which is much faster than using `use!`.
                ctx.Request.Metrics.Counter Metric.FetchInc Map.empty 1L
                let! response = client.SendAsync (request, cancellationToken)
                timer.Stop ()
                ctx.Request.Metrics.Gauge Metric.FetchLatencyUpdate Map.empty (float timer.ElapsedMilliseconds)
                let items = ctx.Request.Items.Add(PlaceHolder.Url, Url request.RequestUri).Add(PlaceHolder.Elapsed, Number timer.ElapsedMilliseconds)
                let! result = next { ctx with Response = response; Request = { ctx.Request with Items = items }}
                response.Dispose ()
                return result
            with
            | ex -> return Panic ex |> Error
        }
