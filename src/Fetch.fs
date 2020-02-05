// Copyright 2019 Cognite AS

namespace Oryx

open System
open System.Net.Http
open System.Threading
open System.Web

open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Collections.Generic
open System.Net.Http.Headers
open Google.Protobuf
open Microsoft.Extensions.Logging

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

        for header, value in acceptHeader :: ctx.Request.Headers do
            if not (client.DefaultRequestHeaders.Contains header) then
                request.Headers.Add (header, value)

        request.Content <- ctx.Request.Content ()
        request

    let logCtx (ctx: HttpContext) (message: string) =
        ctx.Logger |> Option.iter (fun logger ->
            let logLevel = ctx.LoggerLevel |> Option.defaultValue LogLevel.Information
            logger.Log(logLevel, message)
        )

    let logCtxResponse (ctx: HttpContext) (response: HttpResponseMessage) =
        if ctx.Logger.IsSome then
            task {
                let! message = response.Content.ReadAsStringAsync()
                do logCtx ctx message
            }
        else task { return () }

    let logCtxContent (ctx: HttpContext) =
        let content =
            ctx.Request.Content
            |> Option.map (fun content -> content.ToString())
            |> Option.defaultValue ""
        logCtx ctx content

    let fetch<'r, 'err> (next: NextFunc<HttpResponseMessage, 'r, 'err>) (ctx: HttpContext) : HttpFuncResult<'r, 'err> =
        let client =
            match ctx.Request.HttpClient with
            | Some client -> client
            | None -> failwith "Must set httpClient"

        use source = new CancellationTokenSource()
        let cancellationToken =
            match ctx.Request.CancellationToken with
            | Some token -> token
            | None -> source.Token

        do logCtxContent ctx

        task {
            try
                use request = buildRequest client ctx
                // Note: we don't use `use!` for response since the next handler will never throw exceptions. Thus we
                // can dispose ourselves which is much faster than using `use!`.
                let! response = client.SendAsync (request, cancellationToken)
                do! logCtxResponse ctx response

                let! result = next { ctx with Response = response }
                response.Dispose ()
                return result
            with
            | ex -> return Panic ex |> Error
        }
