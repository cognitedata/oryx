// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.Diagnostics
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

type ResponseType =
    | JsonValue
    | Protobuf

type Value =
    | String of string
    | Number of int64
    | Url of Uri

    // Give proper output for logging
    override x.ToString() =
        match x with
        | String str -> str
        | Number num -> num.ToString()
        | Url uri -> uri.ToString()

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

and HttpRequest =
    {
      /// HTTP client to use for sending the request.
      HttpClient: unit -> HttpClient
      /// HTTP method to be used.
      Method: HttpMethod
      /// Getter for content to be sent as body of the request. We use a getter so content may be re-created for
      /// retries.
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
      /// Extra state used to e.g build the URL. Clients are free to utilize this property for adding extra
      /// information to the context.
      Items: Map<string, Value>
      /// The given `HttpCompletionOption` to use for the request.
      CompletionMode: HttpCompletionOption }

type HttpResponse =
    {
      /// Map of received headers
      Headers: Map<string, seq<string>>
      /// Http status code
      StatusCode: HttpStatusCode
      /// True if response is successful
      IsSuccessStatusCode: bool
      /// Reason phrase which typically is sent by servers together with the status code
      ReasonPhrase: string }

type HttpContext =
    { Request: HttpRequest
      Response: HttpResponse }

[<RequireQualifiedAccess>]
module Context =
    let private fileVersion =
        FileVersionInfo
            .GetVersionInfo(
                Assembly.GetExecutingAssembly().Location
            )
            .FileVersion

    let defaultLogFormat =
        "Oryx: {Message} {HttpMethod} {Url}\n→ {RequestContent}\n← {ResponseContent}"

    /// Default request to use.
    let defaultRequest =
        let ua = sprintf "Oryx / v%s (Cognite)" fileVersion

        { HttpClient = (fun () -> failwith "Must set HttpClient")
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
          Metrics = EmptyMetrics()
          Items = Map.empty
          CompletionMode = HttpCompletionOption.ResponseContentRead }

    /// Default response to use.
    let defaultResponse =
        { StatusCode = HttpStatusCode.NotFound
          IsSuccessStatusCode = false
          Headers = Map.empty
          ReasonPhrase = String.Empty }

    /// The default context.
    let defaultContext : HttpContext =
        { Request = defaultRequest
          Response = defaultResponse }

    /// Add HTTP header to context.
    let withHeader (header: string * string) (ctx: HttpContext) =
        { ctx with
              Request =
                  { ctx.Request with
                        Headers = ctx.Request.Headers.Add header } }

    /// Replace all headers in the context.
    let withHeaders (headers: Map<string, string>) (context: HttpContext) =
        { context with
              Request =
                  { context.Request with
                        Headers = headers } }

    /// Helper for setting Bearer token as Authorization header.
    let withBearerToken (token: string) (ctx: HttpContext) =
        let header = ("Authorization", sprintf "Bearer %s" token)

        { ctx with
              Request =
                  { ctx.Request with
                        Headers = ctx.Request.Headers.Add header } }

    /// Set the HTTP client to use for the requests.
    let withHttpClient (client: HttpClient) (ctx: HttpContext) =
        { ctx with
              Request =
                  { ctx.Request with
                        HttpClient = (fun () -> client) } }

    /// Set the HTTP client factory to use for the requests.
    let withHttpClientFactory (factory: unit -> HttpClient) (ctx: HttpContext) =
        { ctx with
              Request =
                  { ctx.Request with
                        HttpClient = factory } }

    /// Set the URL builder to use.
    let withUrlBuilder (builder: HttpRequest -> string) (ctx: HttpContext) =
        { ctx with
              Request =
                  { ctx.Request with
                        UrlBuilder = builder } }

    /// Set a cancellation token to use for the requests.
    let withCancellationToken (token: CancellationToken) (ctx: HttpContext) =
        { ctx with
              Request =
                  { ctx.Request with
                        CancellationToken = token } }

    /// Set the logger (ILogger) to use.
    let withLogger (logger: ILogger) (ctx: HttpContext) =
        { ctx with
              Request =
                  { ctx.Request with
                        Logger = Some logger } }

    /// Set the log level to use (default is LogLevel.None).
    let withLogLevel (logLevel: LogLevel) (context: HttpContext) =
        { context with
              Request =
                  { context.Request with
                        LogLevel = logLevel } }

    /// Set the log format to use.
    let withLogFormat (format: string) (ctx: HttpContext) =
        { ctx with
              Request = { ctx.Request with LogFormat = format } }

    /// Set the log message to use (normally you would like to use the withLogMessage handler instead)
    let withLogMessage (msg: string) (ctx: HttpContext) =
        { ctx with
              Request =
                  { ctx.Request with
                        Items = ctx.Request.Items.Add(PlaceHolder.Message, String msg) } }

    /// Set the metrics (IMetrics) to use.
    let withMetrics (metrics: IMetrics) (ctx: HttpContext) =
        { ctx with
              Request = { ctx.Request with Metrics = metrics } }

    /// Merge the list of context objects. Used by the sequential and concurrent HTTP handlers.
    let merge (ctxs: List<HttpContext>) : HttpContext =
        let ctxs =
            match ctxs with
            | [] -> [ defaultContext ]
            | _ -> ctxs

        // Use the max status code.
        let statusCode =
            ctxs
            |> List.map (fun ctx -> ctx.Response.StatusCode)
            |> List.max

        // Concat the reason phrases (if they are different)
        let reasonPhrase =
            ctxs
            |> List.map (fun ctx -> ctx.Response.ReasonPhrase)
            |> List.distinct
            |> String.concat ", "

        let merge (a: Map<'a, 'b>) (b: Map<'a, 'b>) (f: 'a -> 'b * 'b -> 'b) =
            Map.fold
                (fun s k v ->
                    match Map.tryFind k s with
                    | Some v' -> Map.add k (f k (v, v')) s
                    | None -> Map.add k v s)
                a
                b

        // Merge headers
        let headers =
            ctxs
            |> List.map (fun ctx -> ctx.Response.Headers)
            |> List.fold
                (fun state hdr -> merge state hdr (fun k (a, b) -> if a = b then a else Seq.append a b))
                Map.empty

        { Request =
              ctxs
              |> Seq.map (fun ctx -> ctx.Request)
              |> Seq.head
          Response =
              { Headers = headers
                StatusCode = statusCode
                IsSuccessStatusCode = true
                ReasonPhrase = reasonPhrase } }
