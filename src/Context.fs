// Copyright 2019 Cognite AS

namespace Oryx

open System
open System.Net
open System.Net.Http
open System.Reflection
open System.Threading

open Microsoft.Extensions.Logging

type RequestMethod =
    | POST
    | PUT
    | GET
    | DELETE

type ResponseType = JsonValue | Protobuf

type PropertyBag = Map<string, string>
type UrlBuilder = HttpRequest -> string

and HttpRequest = {
    /// HTTP client to use for sending the request.
    HttpClient: HttpClient option
    /// HTTP method to be used.
    Method: HttpMethod
    /// Getter for content to be sent as body of the request. We use a getter so content may be re-created for retries.
    Content: unit -> HttpContent
    /// Query parameters
    Query: struct (string * string) seq
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
    /// Logger for logging requests
    Logger: ILogger option
    /// LogLevel to log at
    LoggerLevel: LogLevel
}

type Context<'a> = {
    Request: HttpRequest
    Response: 'a
}

type HttpContext = Context<HttpResponseMessage>

module Context =
    let private version =
        let version = Assembly.GetExecutingAssembly().GetName().Version
        {| Major=version.Major; Minor=version.Minor; Build=version.Build |}

    let nullContent = fun () -> null
    let lazyContent content = fun () -> content

    /// Default context to use.
    let defaultRequest =
        let ua = sprintf "Oryx / v%d.%d.%d (Cognite)" version.Major version.Minor version.Build
        {
            HttpClient = None
            Method = HttpMethod.Get
            Content = nullContent
            Query = List.empty
            ResponseType = JsonValue
            Headers = [ "User-Agent", ua ]
            UrlBuilder = fun _ -> String.Empty
            Extra = Map.empty
            CancellationToken = None
            Logger = None
            LoggerLevel = LogLevel.Debug
        }

    let defaultResult =
        new HttpResponseMessage (HttpStatusCode.NotFound)

    let defaultContext : Context<HttpResponseMessage> = {
        Request = defaultRequest
        Response = defaultResult
    }

    let bind (fn: 'a -> Context<'b>) (ctx: Context<'a>) : Context<'b> =
        fn ctx.Response

    /// Add HTTP header to context.
    let addHeader (header: string*string) (context: HttpContext) =
        { context with Request = { context.Request with Headers = header :: context.Request.Headers  } }

    /// Helper for setting Bearer token as Authorization header.
    let setToken (token: string) (context: HttpContext) =
        let header = ("Authorization", sprintf "Bearer %s" token)
        { context with Request = { context.Request with Headers = header :: context.Request.Headers  } }

    let setHttpClient (client: HttpClient) (context: HttpContext) =
        { context with Request = { context.Request with HttpClient = Some client } }

    let setUrlBuilder (builder: HttpRequest -> string) (context: HttpContext) =
        { context with Request = { context.Request with UrlBuilder = builder } }

    let setCancellationToken (token: CancellationToken) (context: HttpContext) =
        { context with Request = { context.Request with CancellationToken = Some token } }

    let setLogger (logger: ILogger) (context: HttpContext) =
        { context with Request = { context.Request with Logger = Some logger } }

    let setLoggerLevel (logLevel: LogLevel) (context: HttpContext) =
        { context with Request = { context.Request with LoggerLevel = logLevel } }
