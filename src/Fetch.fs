// Copyright 2019 Cognite AS

namespace Oryx

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Web

open FSharp.Control.Tasks.V2.ContextInsensitive
open Google.Protobuf
open Newtonsoft.Json
open Thoth.Json.Net

[<AutoOpen>]
module Fetch =

    /// HttpContent implementation to push a JsonValue directly to the output stream.
    type JsonPushStreamContent (content : JsonValue) =
        inherit HttpContent ()
        let _content = content
        do
            base.Headers.ContentType <- MediaTypeHeaderValue "application/json"

        override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
            task {
                use sw = new StreamWriter(stream, UTF8Encoding(false), 1024, true)
                use jtw = new JsonTextWriter(sw, Formatting = Formatting.None)
                do! content.WriteToAsync(jtw)
                do! jtw.FlushAsync()
            } :> _
        override this.TryComputeLength(length: byref<int64>) : bool =
            length <- -1L
            false

    type ProtobufPushStreamContent (content : IMessage) =
        inherit HttpContent ()
        let _content = content
        do
            base.Headers.ContentType <- MediaTypeHeaderValue "application/protobuf"

        override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
            content.WriteTo stream |> Task.FromResult :> _

        override this.TryComputeLength(length: byref<int64>) : bool =
            length <- -1L
            false

    let buildRequest (client: HttpClient) (ctx: HttpContext) : HttpRequestMessage =
        let query = ctx.Request.Query
        let url =
            let result = ctx.Request.UrlBuilder ctx.Request
            if List.isEmpty query
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

        if ctx.Request.Content.IsSome then
            let content =
                match ctx.Request.Content.Value with
                    | CaseJsonValue value -> new JsonPushStreamContent (value) :> HttpContent
                    | CaseProtobuf value -> new ProtobufPushStreamContent (value) :> HttpContent
                    | CaseUrlEncoded values ->
                        let pairs = values |> Seq.map (fun (k, v) -> new KeyValuePair<string, string>(k, v))
                        let content = new FormUrlEncodedContent (pairs)
                        content.Headers.ContentType <- MediaTypeHeaderValue "application/x-www-form-urlencoded"
                        content :> HttpContent
            request.Content <- content
        request

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

        task {
            try
                use message = buildRequest client ctx
                use! response = client.SendAsync(message, cancellationToken)
                return! next { ctx with Response = response }
            with
            | ex -> return Panic ex |> Error
        }
