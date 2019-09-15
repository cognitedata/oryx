// Copyright 2019 Cognite AS

namespace Oryx

open System
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
    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let addQuery (query: (string * string) list) (next: NextFunc<_,_>) (context: HttpContext) =
        next { context with Request = { context.Request with Query = query } }

    /// Add content to context. These content will be added to the HTTP body of
    /// requests that uses this context.
    let setContent (content: Content) (next: NextFunc<_,_>) (context: HttpContext) =
        next { context with Request = { context.Request with Content = Some content } }

    let setResponseType (respType: ResponseType) (next: NextFunc<_,_>) (context: HttpContext) =
        next { context with Request = { context.Request with ResponseType = respType }}

    /// Set the method to be used for requests using this context.
    let setMethod<'a> (method: HttpMethod) (next: NextFunc<HttpResponseMessage,'a>) (context: HttpContext) =
        next { context with Request = { context.Request with Method = method; Content = None } }

    let GET<'a> = setMethod<'a> HttpMethod.Get
    let POST<'a> = setMethod<'a> HttpMethod.Post
    let DELETE<'a> = setMethod<'a> HttpMethod.Delete

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
            content.WriteTo(stream) |> Task.FromResult :> _

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

        let contentHeader =
            match ctx.Request.ResponseType with
            | JsonValue -> "Accept", "application/json"
            | Protobuf -> "Accept", "application/protobuf"

        for header, value in contentHeader :: ctx.Request.Headers do
            if not (client.DefaultRequestHeaders.Contains header) then
                request.Headers.Add (header, value)

        if ctx.Request.Content.IsSome then
            let content =
                match ctx.Request.Content.Value with
                    | CaseJsonValue value -> new JsonPushStreamContent (value) :> HttpContent
                    | CaseProtobuf value -> new ProtobufPushStreamContent (value) :> HttpContent
            request.Content <- content
        request

    let fetch<'a> (next: NextFunc<HttpResponseMessage, 'a>) (ctx: HttpContext) : Task<Context<'a>> =
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
                let! response = client.SendAsync(message, cancellationToken)
                return! next { Request = ctx.Request; Result = Ok response }
            with
            | ex ->
                let error = ResponseError.empty
                return { Request = ctx.Request; Result = Error { error with InnerException = Some ex } }
        }
