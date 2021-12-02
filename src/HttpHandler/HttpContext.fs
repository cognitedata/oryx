// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.Collections
open System.Net
open System.Net.Http
open System.Reflection
open System.Threading

open Microsoft.Extensions.Logging

[<RequireQualifiedAccess>]
type RequestMethod =
    | POST
    | PUT
    | GET
    | DELETE

[<RequireQualifiedAccess>]
type ResponseType =
    | JsonValue
    | Protobuf

[<RequireQualifiedAccess>]
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
      /// Response type. JSON or Protobuf
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
      /// Optional Labels to label the request
      Labels: Generic.IDictionary<string, string>
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
module HttpContext =
    let private fileVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            .InformationalVersion

    let defaultLogFormat =
        "Oryx: {Message} {HttpMethod} {Url}\n→ {RequestContent}\n← {ResponseContent}"

    /// Default request to use.
    let defaultRequest =
        let ua = sprintf "Oryx / v%s (Cognite)" fileVersion

        { HttpClient = (fun () -> failwith "Must set HttpClient")
          Method = HttpMethod.Get
          ContentBuilder = None
          Query = List.empty
          ResponseType = ResponseType.JsonValue
          Headers = [ "User-Agent", ua ] |> Map
          UrlBuilder = fun _ -> String.Empty
          CancellationToken = CancellationToken.None
          Logger = None
          LogLevel = LogLevel.None
          LogFormat = defaultLogFormat
          Metrics = EmptyMetrics()
          Labels = dict []
          Items = Map.empty
          CompletionMode = HttpCompletionOption.ResponseContentRead }

    /// Default response to use.
    let defaultResponse =
        { StatusCode = HttpStatusCode.NotFound
          IsSuccessStatusCode = false
          Headers = Map.empty
          ReasonPhrase = String.Empty }

    /// The default context.
    let defaultContext: HttpContext =
        { Request = defaultRequest
          Response = defaultResponse }

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
                (fun state hdr -> merge state hdr (fun _ (a, b) -> if a = b then a else Seq.append a b))
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
