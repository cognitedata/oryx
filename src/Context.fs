// Copyright 2019 Cognite AS

namespace Oryx

open System
open System.Net
open System.Net.Http
open System.Reflection

open Thoth.Json.Net
open System.Threading

type RequestMethod =
    | POST
    | PUT
    | GET
    | DELETE

type Content =
    internal
    | CaseJsonValue of JsonValue
    | CaseProtobuf of Google.Protobuf.IMessage

    static member JsonValue jsonValue = CaseJsonValue jsonValue
    static member Protobuf protobuf = CaseProtobuf protobuf

type ResponseType = JsonValue | Protobuf

type PropertyBag = Map<string, string>
type UrlBuilder = HttpRequest -> string

and HttpRequest = {
    /// HTTP client to use for sending the request.
    HttpClient: HttpClient option
    /// HTTP method to be used.
    Method: HttpMethod
    /// Content to be sent as body of the request.
    Content: Content option
    /// Query parameters
    Query: (string * string) list
    /// Responsetype. JSON or Protobuf
    ResponseType: ResponseType
    /// List of headers to be sent
    Headers: (string * string) list
    /// A function that builds the request URL based on the collected extra info.
    UrlBuilder: UrlBuilder
    /// Extra info used to build the URL
    Extra: PropertyBag
    /// Optional CancellationToken for cancelling the request.
    CancellationToken: CancellationToken option
}

type Context<'a> = {
    Request: HttpRequest
    Result: Result<'a, ResponseError>
}

type HttpContext = Context<HttpResponseMessage>

module Context =
    let private version =
        let version = Assembly.GetExecutingAssembly().GetName().Version
        {| Major=version.Major; Minor=version.Minor; Build=version.Build |}

    /// Default context to use.
    let internal defaultRequest =
        let ua = sprintf "Oryx / v%d.%d.%d (Cognite)" version.Major version.Minor version.Build
        {
            HttpClient = None
            Method = HttpMethod.Get
            Content = None
            Query = List.empty
            ResponseType = JsonValue
            Headers = [
                "User-Agent", ua
            ]
            UrlBuilder = fun _ -> String.Empty
            Extra = Map.empty
            CancellationToken = None
        }

    let internal defaultResult =
        Ok (new HttpResponseMessage (HttpStatusCode.NotFound))

    let defaultContext : Context<HttpResponseMessage> = {
        Request = defaultRequest
        Result = defaultResult
    }

    let bind fn ctx =
        match ctx.Result with
        | Ok res ->
            fn res
        | Error err ->
            { Request = ctx.Request; Result = Error err }

    let bindAsync (fn: Context<'a> -> Async<Context<'b>>) (a: Async<Context<'a>>) : Async<Context<'b>> =
        async {
            let! p = a
            match p.Result with
            | Ok _ ->
                return! fn p
            | Error err ->
                return { Request = p.Request; Result = Error err }
        }

    /// Add HTTP header to context.
    let addHeader (header: string*string) (context: HttpContext) =
        { context with Request = { context.Request with Headers = header :: context.Request.Headers  } }

    /// Helper for setting Bearer token as Authorization header.
    let setToken (token: string) (context: HttpContext) =
        let header = ("Authorization", sprintf "Bearer: %s" token)
        { context with Request = { context.Request with Headers = header :: context.Request.Headers  } }

    let setHttpClient (client: HttpClient) (context: HttpContext) =
        { context with Request = { context.Request with HttpClient = Some client } }

    let setUrlBuilder (builder: HttpRequest -> string) (context: HttpContext) =
        { context with Request = { context.Request with UrlBuilder = builder } }

    let setCancellationToken (token: CancellationToken) (context: HttpContext) =
        { context with Request = { context.Request with CancellationToken = Some token } }
