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

type Value =
    | String of string
    | Number of int64
    | Url of Uri

    // Give proper output for logging
    override x.ToString () =
        match x with
        | String str -> str
        | Number num -> num.ToString ()
        | Url uri -> uri.ToString ()


module PlaceHolder =
    [<Literal>]
    let HttpMethod = "HttpMethod"

    [<Literal>]
    let RequestContent = "RequestContent"

    [<Literal>]
    let ResponseContent = "ResponseContent"

    [<Literal>]
    let Message = "Message"

    [<Literal>]
    let Url = "Url"

    [<Literal>]
    let Elapsed = "Elapsed"


type UrlBuilder = HttpRequest -> string

and HttpRequest = {
    /// HTTP client to use for sending the request.
    HttpClient: unit -> HttpClient
    /// HTTP method to be used.
    Method: HttpMethod
    /// Getter for content to be sent as body of the request. We use a getter so content may be re-created for retries.
    ContentBuilder: (unit -> HttpContent) option
    /// Query parameters
    Query: seq<struct (string * string)>
    /// Responsetype. JSON or Protobuf
    ResponseType: ResponseType
    /// Map of headers to be sent
    Headers: Map<string, string>
    /// A function that builds the request URL based on the collected extra info.
    UrlBuilder: UrlBuilder
    /// Optional CancellationToken for cancelling the request.
    CancellationToken: CancellationToken
    /// Optional Logger for logging requests.
    Logger: ILogger option
    /// The LogLevel to log at
    LogLevel: LogLevel
    /// Logging format string
    LogFormat: string
    /// Optional Metrics for recording metrics.
    Metrics: IMetrics
    /// Extra state used to e.g build the URL. Clients are free to utilize this property for adding extra information to
    /// the context.
    Items: Map<string, Value>
    CompletionMode : HttpCompletionOption
}

type Context<'T> = {
    Request: HttpRequest
    Response: 'T
}

type HttpContext = Context<HttpResponseMessage>

module Context =
    let private version =
        let version = Assembly.GetExecutingAssembly().GetName().Version
        {| Major=version.Major; Minor=version.Minor; Build=version.Build |}

    let defaultLogFormat = "Oryx: {Message} {HttpMethod} {Url}\n→ {RequestContent}\n← {ResponseContent}"

    /// Default context to use.
    let defaultRequest =
        let ua = sprintf "Oryx / v%d.%d.%d (Cognite)" version.Major version.Minor version.Build
        {
            HttpClient = (fun () -> failwith "Must set HttpClient")
            Method = HttpMethod.Get
            ContentBuilder = None
            Query = List.empty
            ResponseType = JsonValue
            Headers = [ "User-Agent", ua ] |> Map
            UrlBuilder = fun _ -> String.Empty
            CancellationToken = CancellationToken.None
            Logger = None
            LogLevel = LogLevel.None
            LogFormat = defaultLogFormat
            Metrics = EmptyMetrics ()
            Items = Map.empty
            CompletionMode = HttpCompletionOption.ResponseContentRead
        }

    let defaultResult = new HttpResponseMessage (HttpStatusCode.NotFound)

    /// The default context.
    let defaultContext : Context<HttpResponseMessage> = {
        Request = defaultRequest
        Response = defaultResult
    }

    /// Add HTTP header to context.
    let withHeader (header: string*string) (context: HttpContext) =
        { context with Request = { context.Request with Headers = context.Request.Headers.Add header } }

    /// Replace all headers in the context.
    let withHeaders (headers: Map<string,string>) (context: HttpContext) =
        { context with Request = { context.Request with Headers = headers } }

    /// Helper for setting Bearer token as Authorization header.
    let withBearerToken (token: string) (context: HttpContext) =
        let header = ("Authorization", sprintf "Bearer %s" token)
        { context with Request = { context.Request with Headers = context.Request.Headers.Add header  } }

    /// Set the HTTP client to use for the requests.
    let withHttpClient (client: HttpClient) (context: HttpContext) =
        { context with Request = { context.Request with HttpClient = (fun () -> client) } }

    /// Set the HTTP client factory to use for the requests.
    let withHttpClientFactory (factory: unit -> HttpClient) (context: HttpContext) =
        { context with Request = { context.Request with HttpClient = factory } }

    /// Set the URL builder to use.
    let withUrlBuilder (builder: HttpRequest -> string) (context: HttpContext) =
        { context with Request = { context.Request with UrlBuilder = builder } }

    /// Set a cancellation token to use for the requests.
    let withCancellationToken (token: CancellationToken) (context: HttpContext) =
        { context with Request = { context.Request with CancellationToken = token } }

    /// Set the logger (ILogger) to use.
    let withLogger (logger: ILogger) (context: HttpContext) =
        { context with Request = { context.Request with Logger = Some logger } }

    /// Set the log level to use (default is LogLevel.None).
    let withLogLevel (logLevel: LogLevel) (context: HttpContext) =
        { context with Request = { context.Request with LogLevel = logLevel } }

    /// Set the log format to use.
    let withLogFormat (format: string) (context: HttpContext) =
        { context with Request = { context.Request with LogFormat = format } }

    /// Set the log message to use (normally you would like to use the withLogMessage handler instead)
    let withLogMessage (msg: string) (context: HttpContext) =
        { context with Request = { context.Request with Items = context.Request.Items.Add (PlaceHolder.Message, String msg) } }

    /// Set the metrics (IMetrics) to use.
    let withMetrics (metrics: IMetrics) (context: HttpContext) =
        { context with Request = { context.Request with Metrics = metrics } }
