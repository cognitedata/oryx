namespace Oryx

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.IO
open System.Text
open System.Threading.Tasks
open System.Web

open FSharp.Control.Tasks.V2.ContextInsensitive
open Google.Protobuf
open Newtonsoft.Json
open Thoth.Json.Net

[<AutoOpen>]
module Fetch =
    /// **Description**
    ///
    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    ///
    /// **Parameters**
    ///   * `query` - List of tuples (name, value)
    ///   * `context` - The context to add the query to.
    ///
    let addQuery (query: (string * string) list) (next: NextHandler<_,_>) (context: HttpContext) =
        next { context with Request = { context.Request with Query = query } }

    let setContent (content: Content) (next: NextHandler<_,_>) (context: HttpContext) =
        next { context with Request = { context.Request with Content = Some content } }

    let setResponseType (respType: ResponseType) (next: NextHandler<_,_>) (context: HttpContext) =
        next { context with Request = { context.Request with ResponseType = respType }}

    /// **Description**
    ///
    /// Set the method to be used for requests using this context.
    ///
    /// **Parameters**
    ///   * `method` - Method is a parameter of type `Method` and can be
    ///     `Put`, `Get`, `Post` or `Delete`.
    ///   * `context` - parameter of type `Context`
    ///
    /// **Output Type**
    ///   * `Context`
    ///
    let setMethod<'a> (method: HttpMethod) (next: NextHandler<HttpResponseMessage,'a>) (context: HttpContext) =
        next { context with Request = { context.Request with Method = method; Content = None } }

    let GET<'a> = setMethod<'a> HttpMethod.Get
    let POST<'a> = setMethod<'a> HttpMethod.Post
    let DELETE<'a> = setMethod<'a> HttpMethod.Delete

    let decodeStreamAsync (decoder : Decoder<'a>) (stream : IO.Stream) =
        task {
            use tr = new StreamReader(stream) // StreamReader will dispose the stream
            use jtr = new JsonTextReader(tr)
            let! json = Newtonsoft.Json.Linq.JValue.ReadFromAsync jtr

            return Decode.fromValue "$" decoder json
        }

    /// HttpContent implementation to push a JsonValue directly to the output stream.
    type JsonPushStreamContent (content : JsonValue) =
        inherit HttpContent ()
        let _content = content
        do
            base.Headers.ContentType <- MediaTypeHeaderValue("application/json")
        override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
            task {
                use sw = new StreamWriter(stream, UTF8Encoding(false), 1024, true)
                use jtw = new JsonTextWriter(sw, Formatting = Formatting.None)
                jtw.Formatting <- Formatting.None
                do! content.WriteToAsync(jtw)
                do! jtw.FlushAsync()
                return ()
            } :> _
        override this.TryComputeLength(length: byref<int64>) : bool =
            length <- -1L
            false

    type ProtobufPushStreamContent (content : IMessage) =
        inherit HttpContent ()
        let _content = content
        do
            base.Headers.ContentType <- MediaTypeHeaderValue("application/protobuf")
        override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
            task {
                content.WriteTo(stream)
            } :> _
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
            | JsonValue -> ("Accept", "application/json")
            | Protobuf -> ("Accept", "application/protobuf")

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

    let sendRequest (request: HttpRequestMessage) (client: HttpClient) : Task<Result<Stream, ResponseError>> =
        task {
            try
                let! response = client.SendAsync request
                let! stream = response.Content.ReadAsStreamAsync ()
                if response.IsSuccessStatusCode then
                    return Ok stream
                else
                    let! result = decodeStreamAsync ApiResponseError.Decoder stream
                    match result with
                    | Ok apiResponseError ->
                        return apiResponseError.Error |> Error
                    | Error message ->
                        return {
                            ResponseError.empty with
                                Code = int response.StatusCode
                                Message = message
                        } |> Error
            with
            | ex ->
                return {
                    ResponseError.empty with
                        Code = 400
                        Message = ex.Message
                        InnerException = Some ex
                } |> Error
        }

    let fetch<'a> (next: NextHandler<IO.Stream, 'a>) (ctx: HttpContext) : Async<Context<'a>> =
        async {
            let client =
                match ctx.Request.HttpClient with
                | Some client -> client
                | None -> failwith "Must set httpClient"
            use message = buildRequest client ctx

            let! result = sendRequest message client |> Async.AwaitTask
            if ctx.Request.Content.IsSome then
                message.Content.Dispose ()
            return! next { Request = ctx.Request; Result = result }
        }

    /// Handler for disposing a stream when it's not needed anymore.
    let dispose<'a> (next: NextHandler<unit,'a>) (context: Context<Stream>) =
        async {
            let nextResult = context.Result |> Result.map (fun stream -> stream.Dispose ())
            return! next { Request = context.Request; Result = nextResult }
        }
