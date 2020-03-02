# Oryx

[![Build](https://img.shields.io/travis/cognitedata/oryx)](https://travis-ci.org/cognitedata/oryx)
[![codecov](https://codecov.io/gh/cognitedata/oryx/branch/master/graph/badge.svg)](https://codecov.io/gh/cognitedata/oryx)
[![Nuget](https://img.shields.io/nuget/vpre/oryx)](https://www.nuget.org/packages/Oryx/)

Oryx is a high performance .NET cross platform functional HTTP request handler library for writing HTTP clients and
orchestrating web requests in F#.

> An SDK for writing HTTP web clients and orchestrating web requests.

This library enables you to write Web and REST clients and SDKs for various APIs and is currently used by the [.NET SDK
for Cognite Data Fusion (CDF)](https://github.com/cognitedata/cognite-sdk-dotnet).

Oryx is heavily inspired by the [Giraffe](https://github.com/giraffe-fsharp/Giraffe) web framework, and applies the same
ideas to the client making the web requests. You can think of Oryx as the client equivalent of Giraffe, and you could
envision the HTTP request processing pipeline starting at the client and going all the way to the server and back again.

## Fundamentals

The main building blocks in Oryx is the `Context` and the `HttpHandler`. The Context contains all the state needed for
making the request, and also contains any response (or error) received from the remote server:

```fs
type Context<'a> = {
    Request: HttpRequest
    Response: 'a
}
```

The `Context` is constructed synchronously using a series of context builder functions (`Context -> Context`). But it may also be transformed by series of asynchronous HTTP handlers. The `HttpHandler` takes a `Context` (and a `NextFunc`) and
returns a new `Context` wrapped in a `Result` and a `Task`.

```fs
type HttpFuncResult<'r, 'err> =  Task<Result<Context<'r>, HandlerError<'err>>>

type HttpFunc<'a, 'r, 'err> = Context<'a> -> HttpFuncResult<'r, 'err>
type NextFunc<'a, 'r, 'err> = HttpFunc<'a, 'r, 'err>

type HttpHandler<'a, 'b, 'r, 'err> = NextFunc<'b, 'r, 'err> -> Context<'a> -> HttpFuncResult<'r, 'err>

// For convenience
type HttpHandler<'a, 'r, 'err> = HttpHandler<'a, 'a, 'r, 'err>
type HttpHandler<'r, 'err> = HttpHandler<HttpResponseMessage, 'r, 'err>
type HttpHandler<'err> = HttpHandler<HttpResponseMessage, 'err>
```

An `HttpHandler` is a plain function that takes two curried arguments, a `NextFunc` and a `Context`, and returns a new
`Context` (wrapped in a `Result` and `Task`) when finished. On a high level the `HttpHandler` function takes and returns
a context object, which means every `HttpHandler` function has full control of the outgoing `Request` and also the
resulting `Response`.

Each HttpHandler usually adds more info to the `HttpRequest` before passing it further down the pipeline by invoking the
next `NextFunc` or short circuit the execution by returning a result of `Result<Context<'a>, ResponseError>`. E.g if an
HttpHandler detects an error, then it can return `Result.Error` to fail the processing.

The easiest way to get your head around a Oryx `HttpHandler` is to think of it as a functional web request processing
pipeline. Each handler has the full `Context` at its disposal and can decide whether it wants to fail the request or
continue the request by passing on a new context to the "next" handler.

1. Call the next handler `NextFunc` with a result value (`'a`), and return (`return!`) what the next handler is
   returning. Here you have the option to eliding the await by just synchronously return (`return`) the `Task` returned by the
   `next` function.
2. Return an `Error`result to short circuit the processing and fail the request.
3. It is technically possible to also return `Ok` to short circuit the processing, but this is not something you would normally do.

## Context Builders

The context you want to use for your requests may constructed using a builder like pattern (`Context -> Context`) where you set the common things you need for your request. These are synchronous functions where you can set the headers you want to use, the HTTP client, URL builder, logging and metrics.

- `defaultContext` - A default empty context.

The following builder functions may be used:

- `withHeader` - Adds a header to the context.
- `withHeaders` - Adds headers to the context.
- `withBearerToken` - Adds an `Authorization` header with `Bearer` token.
- `withHttpClient` - Adds the `HttpClient` to use for making requests using the `fetch` handler.
- `withHttpClientFactory` - Adds an `HttpClient` factory function to use for producing the `HttpClient`.
- `withUrlBuilder` - Adds an the URL builder to use. An URL builder construct the URL for the `Request` part of the context.
- `withCancellationToken` - Adds a cancellation token to use for the context. This is particularly useful when using Oryx together with C# client code that supplies a cancellation token.
- `withLogger` - Adds an [`ILogger`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.ilogger?view=dotnet-plat-ext-3.1) for logging requests and responses.
- `withLogLevel` - The log level ([`LogLevel`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.loglevel?view=dotnet-plat-ext-3.1)) that the logging should be performed at. Oryx will disable logging for `LogLevel.None` and this is also the default log level.
- `withLogFormat` - Specify the log format of the log messages written.
- `withMetrics` - Add and `IMetrics` interface to produce metrics info.

## HTTP Handlers

The context may then be transformed using HTTP handlers. HTTP handlers are like lego bricks and may be composed into more complex HTTP handlers. The HTTP handlers included with Oryx are:

- `catch` - Catches errors and continue using another handler.
- `chunk` - Chunks a sequence of HTTP handlers into sequential and concurrent batches.
- `concurrent` - Runs a sequence of HTTP handlers concurrently.
- `extractHeader` - Extract header from the HTTP response.
- `fetch` - Fetches from remote using current context
- `log` - Log information about the given request.
- `logWithMessage` - Log information about the given request supplying a user specified message.
- `parse` - Parse response stream to a user specified type synchronously.
- `parseAsync` - Parse response stream to a user specified type asynchronously.
- `retry` - Retries the current HTTP chandler if an error occurs.
- `sequential` - Runs a sequence of HTTP handlers sequentially.
- `withContent` - Add HTTP content to the fetch request
- `withMethod` - with HTTP method. You can use GET, PUT, POST instead.
- `withQuery` - Add URL query parameters
- `withResponseType` - Sets the Accept header of the request.
- `withUrl` - Use the given URL for the request.
- `withUrlBuilder` - Use the given URL builder for the request.
- `withError` - Detect if the HTTP request failed, and then fail processing.

In addition there are several extension for decoding JSON and Protobuf responses:

- `json` - Decodes the given `application/json` response into a user specified type.
- `protobuf` - - Decodes the given `application/protobuf` response into a Protobuf specific type.

See [JSON and Protobuf Content Handling](#json-and-protobuf-content-handling) for more information.

### HTTP verbs

The HTTP verbs are convenience functions using the `withMethod` under the hood:

- `GET` - HTTP get request
- `PUT` - HTTP put request
- `POST` - HTTP post request
- `DELETE` - HTTP delete request
- `OPTIONS` - HTTP options request

## Composition

The fact that everything is an `HttpHandler` makes it easy to compose handlers together. You can think of them as lego
bricks that you can fit together. Two or more `HttpHandler` functions may be composed together using Kleisli
composition, i.e using the fish operator `>=>`.

```fs
let (>=>) a b = compose a b
```

The `compose` function is the magic that sews it all together and explains how we curry the `HttpHandler` to generate a new `NextFunc` that we give to next `HttpHandler`.

```fs
    let compose (first : HttpHandler<'a, 'b, 'r, 'err>) (second : HttpHandler<'b, 'c, 'r, 'err>) : HttpHandler<'a,'c,'r, 'err> =
        fun (next: NextFunc<'c, 'r, 'err>) ->
            let func =
                next
                |> second
                |> first

            func
```
One really amazing thing with Oryx is that we can simplify this complex function using [η-conversion](https://wiki.haskell.org/Eta_conversion). Thus dropping both `ctx` and `next`, making composition into a basic functional compose that we alias using the fish operator (`>=>`):

```fs
    let compose = second >> first

    let (>=>) = compose
```

This enables you to compose your web requests and decode the response, e.g as we do when listing Assets in the
[Cognite Data Fusion SDK](https://github.com/cognitedata/cognite-sdk-dotnet/blob/master/Oryx.Cognite/src/Handler.fs):

```fs
    let list (query: AssetQuery) : HttpHandler<HttpResponseMessage, ItemsWithCursor<AssetReadDto>, 'a> =
        let url = Url +/ "list"

        POST
        >=> withVersion V10
        >=> withResource url
        >=> withContent (() -> new JsonPushStreamContent<AssetQuery>(query, jsonOptions))
        >=> fetch
        >=> withError decodeError
        >=> json jsonOptions
```

Thus the function `listAssets` is now also an `HttpHandler` and may be composed with other handlers to create complex chains for doing multiple requests in series (or concurrently) to a web service.

## Retrying Requests

Since Oryx is based on `HttpClient`, you may use [Polly](https://github.com/App-vNext/Polly) handling resilience. For simpler retrying there is also a `retry` handler that retries the `next` HTTP
handler using max number of retries and exponential backoff.

```fs
val retry:
   shouldRetry : HandlerError<'err> -> bool ->
   initialDelay: int<ms>              ->
   maxRetries  : int                  ->
   next        : NextFunc<'a,'r,'err> ->
   ctx         : Context<'a>
              -> HttpFuncResult<'r,'err>
```

The `shouldRetry` handler takes the `HandlerError<'err>` and should return `true` if the request should be retried e.g here is an example used from the Cognite .NET SDK:

```fs
let retry (initialDelay: int<ms>) (maxRetries : int) (next: NextFunc<'a,'r>) (ctx: Context<'a>) : HttpFuncResult<'r> =
    let shouldRetry (error: HandlerError<ResponseException>) : bool =
        match error with
        | ResponseError err ->
            match err.Code with
            // Rate limiting
            | 429 -> true
            // 500 is hard to say, but we should avoid having those in the api. We get random and transient 500
            // responses often enough that it's worth retrying them.
            | 500 -> true
            // 502 and 503 are usually transient.
            | 502 -> true
            | 503 -> true
            // Do not retry other responses.
            | _ -> false
        | Panic err ->
            match err with
            | :? Net.Http.HttpRequestException
            | :? System.Net.WebException -> true
            // do not retry other exceptions.
            | _ -> false

    retry shouldRetry initialDelay maxRetries next ctx
```

Now you can simplify the retry handling by partially applying the retry count and the initial retry delay:

```fs
let RetryCount = 3

let InitialRetryDelay = 500<ms>

let retry next ctx = retry InitialRetryDelay RetryCount next ctx
```

This makes retrying a handler very compact, e.g:

```fs
retry >=> req {
    let! result = Assets.list query
    return result.Items |> Seq.map AssetEntity.Create, Some result.NextCursor
}
```

## Concurrent and Sequential Handlers

A `sequential` operator for running a list of HTTP handlers in sequence.

```fs
val sequential : (handlers : HttpHandler<'a, 'b, 'b, 'err> seq) -> (next: NextFunc<'b list, 'r, 'err>) -> (ctx: Context<'a>) -> HttpFuncResult<'r, 'err>
```

And a `concurrent` operator that runs a list of HTTP handlers in parallel.

```fs
val concurrent : (handlers: HttpHandler<'a, 'b, 'b> seq) -> (next: NextFunc<'b list, 'c>) -> (ctx: Context<'a>) -> Task<Context<'c>>
```

You can also combine sequential and concurrent requests by chunking the request. The `chunk` handler uses `chunkSize`
and `maxConcurrency` to decide how much will be done in parallel. It takes a list of items and a handler that transforms
these items into HTTP handlers. This is really nice if you need to e.g read thousands of items from a service in multiple
requests.

```fs
val chunk:
   chunkSize     : int     ->
   maxConcurrency: int     ->
   handler       : seq<'a> -> HttpHandler<HttpResponseMessage,seq<'b>,seq<'b>,'err> ->
   items         : seq<'a>
                -> HttpHandler<HttpResponseMessage,seq<'b>,'r,'err>
```

Note that chunk will fail if one of the inner requests fails so for e.g a writing scenario you most likely want to create your own custom chunk operator that have different error semantics. If you write such operators then feel free to open a PR so we can include them in the library.

## Error handling

Errors are handled by the main handler logic. Every HTTP handler returns `Task<Result<Context<'r>,
HandlerError<'err>>>`. Thus every stage in the pipeline may be short-circuit by `Error`, or be continued by `Ok`. The
error type is generic and needs to be set by the client SDK or application. Oryx don't know anything about how to decode
the `ResponseError`.

```fs
type HandlerError<'err> =
    /// Request failed with some exception, e.g HttpClient throws an exception, or JSON decode error.
    | Panic of exn
    /// User defined error response, e.g decoded error response from the API service.
    | ResponseError of 'err
```

To produce a custom error response you can use the `withError` handler _after_ e.g `fetch`. The supplied `errorHandler`
is given full access the the `HttpResponseMessage` and may produce a custom `ErrorRespose`, or fail with `Panic` if
decoding fails.

```fs
val withError<'a, 'r, 'err> (errorHandler : HttpResponseMessage -> Task<HandlerError<'err>>) -> (next: NextFunc<HttpResponseMessage,'r, 'err>) -> (context: HttpContext) -> HttpFuncResult<'r, 'err>
```

It's also possible to catch errors using the `catch` handler _before_ e.g `fetch`. The function takes an `errorHandler`
that is given the returned error and produces a new `next` continuation that may then decide to return `Ok` instead of
`Error`. This is very helpful when a failed request not necessarily means error, e.g if you need to check if an object
with a given id exist at the server.

```fs
val catch : (errorHandler: HandlerError<'err> -> NextFunc<'a, 'r, 'err>) -> (next: HttpFunc<'a, 'r, 'err>) -> (ctx : Context<'a>) -> HttpFuncResult<'r, 'err>
```

## JSON and Protobuf Content Handling

Oryx can serialize (and deserialize) content using:

- [`System.Text.Json`](https://docs.microsoft.com/en-us/dotnet/api/system.text.json?view=netcore-3.1)
- [`Newtonsoft.Json`](https://www.newtonsoft.com/json)
- [`Thoth.Json.Net`](https://github.com/thoth-org/Thoth.Json.Net)
- [`Google.Protobuf`](https://developers.google.com/protocol-buffers)

### System.Text.Json

Support for `System.Text.Json` is made available using the [`Oryx.SystemTextJson`](https://www.nuget.org/packages/Oryx.SystemTextJson/) extension.

The `json` decode HTTP handler takes a `JsonSerializerOptions` to decode the response into user defined type of `'a`.

```fs
val json<'a, 'r, 'err> (options: JsonSerializerOptions) -> HttpHandler<HttpResponseMessage, 'a, 'r, 'err>
```

Content can be handled using `type JsonPushStreamContent<'a> (content : 'a, options : JsonSerializerOptions)`.

### Newtonsoft.Json

Support for `Newtonsoft.Json` is made available using the [`Oryx.NewtonsoftJson`](https://www.nuget.org/packages/Oryx.NewtonsoftJson/) extension.

The `json` decode HTTP handler decodes the response into user defined type of `'a`.

```fs
val json<'a, 'r, 'err> (next: NextFunc<'a,'r, 'err>) -> (context: HttpContext) -> HttpFuncResult<'r, 'err>
```

Content can be handled using `type JsonPushStreamContent (content : JToken)`.

### Thoth.Json.Net

Support for `Thoth.Net.Json` is made available using the [`Oryx.ThothNetJson`](https://www.nuget.org/packages/Oryx.Protobuf/) extension.

The `json` decoder takes a `Decoder` from `Thoth.Json.Net` to decode the response into user defined type of `'b`.

```fs
val json<'a, 'r, 'err> : (decoder : Decoder<'a>) -> (next: NextFunc<'a,'r, 'err>) -> (context: HttpContext) -> HttpFuncResult<'r, 'err>
```

Content can be handled using `type JsonPushStreamContent (content : JsonValue)`.

### Protobuf

Protobuf support is made available using the [`Oryx.Protobuf`](https://www.nuget.org/packages/Oryx.ThothJsonNet/) extension.

The `protobuf` decoder takes a `Stream -> 'b` usually generated by ``. to decode the response into user defined type of `'b`.

```fs
val protobuf<'b, 'r, 'err> : (parser : Stream -> 'b) -> (next: NextFunc<'b, 'r, 'err>) -> (context : Context<HttpResponseMessage>) -> Task<Result<Context<'r>,HandlerError<'err>>>
```

Both encode and decode uses streaming all the way so no large strings or arrays will be allocated in the process.

Content can be handled using `type ProtobufPushStreamContent (content : IMessage)`.

## Computational Expression Builder

Working with `Context` objects can be a bit painful since the actual result will be available inside a `Task` effect
that has a `Result` that can be either `Ok` of the actual response, or `Error`. To make it simpler to handle multiple
requests using handlers you can use the `req` builder that will hide the complexity of both the `Context` and the
`Result`.

```fs
req {
    let! assetDto = Assets.Entity.get key

    let asset = assetDto |> Asset.FromAssetReadDto
    if expands.Contains("Parent") && assetDto.ParentId.IsSome then
        let! parentDto = Assets.Entity.get assetDto.ParentId.Value
        let parent = parentDto |> Asset.FromAssetReadDto
        let expanded = { asset with Parent = Some parent }
        return expanded
    else
        return asset
}
```

The request may then be composed with other handlers, e.g chunked, retried, and/or logged.

To run a handler you can use the `runAsync` function.

```fs
let runAsync (handler: HttpHandler<'a,'r,'r, 'err>) (ctx : Context<'a>) : Task<Result<'r, HandlerError<'err>>>
```

## Logging and Metrics

Oryx supports logging using the logging handlers. To setup for logging you first need to enable logging in the context by both setting a logger of type `ILogger` ([Microsoft.Extensions.Logging](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.ilogger?view=dotnet-plat-ext-3.1)) and the logging level to something higher than `LogLevel.None`.

```fs
val withLogger : (logger: ILogger) -> (context: HttpContext) -> (context: HttpContext)

val withLogLevel : (logLevel: LogLevel) -> (context: HttpContext) -> (context: HttpContext)

val withLogFormat (format: string) (context: HttpContext) -> (context: HttpContext)
```

The default format string is:

`"Oryx: {Message} {HttpMethod} {Uri}\n{RequestContent}\n{ResponseContent}"`

You can also use a custom log format string by setting the log format using `withLogFormat`. The available place holders you may use are:

- `Elapsed` - The elapsed request time for `fetch` in milliseconds.
- `HttpMethod` - The HTTP method used, i.e `PUT`, `GET`, `POST`, `DELETE` or `PATCH`.
- `Message` - A user supplied message using `logWithMessage`.
- `ResponseContent` - The response content received. Must implement `ToString` to give meaningful output.
- `RequestContent` - The request content being sent. Must implement `ToString` to give meaningful output.
- `Url` - The URL used for fetching.

**Note:** Oryx will not call `.ToString ()` but will hand it over to the `ILogger` for the actual string interpolation, given that the message will actually end up being logged.

There are to logging handlers. One without a message and one where you can supply a custom message that may be added to the logging output (see format string). The logging handlers do not alter the types of the pipeline and may be composed anywhere. But to give meaningful output they should be composed after fetching (`fetch`).

```fs
val log : next: HttpFunc<'a, 'r, 'err> -> ctx : Context<'a>  -> HttpFuncResult<'r, 'err>

val logWithMessage : msg: string -> next: HttpFunc<'a, 'r, 'err> -> ctx : Context<'a> -> HttpFuncResult<'r, 'err>
```

Oryx may also emit metrics using the `IMetrics` interface (Oryx specific) that you can use with e.g Prometheus.

```fs
type IMetrics =
    abstract member Counter : metric: string -> labels: IDictionary<string, string> -> increase: int64 -> unit
    abstract member Gauge : metric: string -> labels: IDictionary<string, string> -> value: float -> unit
```

The currently defined Metrics are:

- `Metric.FetchInc` - ("MetricFetchInc") The increase in the number of fetches when using the `fetch` handler.
- `Metric.FetchErrorInc` - ("MetricFetchErrorInc"). The increase in the number of fetch errors when using the `fetch` handler.
- `Metrics.FetchRetryInc` - ("MetricsFetchRetryInc"). The increase in the number of retries when using the `retry` handler.
- `Metric.FetchLatencyUpdate` - ("MetricFetchLatencyUpdate"). The update in fetch latency (in milliseconds) when using the `fetch` handler.
- `Metric.DecodeErrorInc` - ("Metric.DecodeErrorInc"). The increase in decode errors when using a `json` decode handler.

Labels are currently not set but are added for future use, e.g setting the error code for fetch errors etc.

## Extending Oryx

It's easy to extend Oryx with your own context builders and HTTP handlers. Everything is functions so you

```fs
let withResource (resource: string) (next: NextFunc<_,_>) (context: HttpContext) =
    next { context with Request = { context.Request with Extra = context.Request.Extra.Add("resource", String resource) } }

let withVersion (version: ApiVersion) (next: NextFunc<_,_>) (context: HttpContext) =
    next { context with Request = { context.Request with Extra = context.Request.Extra.Add("apiVersion", String (version.ToString ())) } }
```

The handlers above will add custom values to the context that may be used by the supplied URL builder. Note that anything added to the `Extra` property bag is also available as place-holders in the logging format string.

```fs
let urlBuilder (request: HttpRequest) : string =
    let extra = request.Extra
    ...
```

## Differences from Giraffe

Oryx and Giraffe is build on the same ideas of using HTTP handlers. The difference is that Oryx is for clients while Giraffe is for servers.

In addition:

The Oryx `HttpHandler` is generic both on the response and error types. This means that you may decode the response or
the error response to user defined types within the pipeline itself.

```fs
type HttpHandler<'a, 'b, 'r, 'err> = NextFunc<'b, 'r, 'err> -> Context<'a> -> HttpFuncResult<'r, 'err>
```

So an `HttpHandler` takes a context of `'a`. The handler itself transforms the context from `'a` to `'b`. Then the next
handler continuation transforms from `'b` to `'r`, and the handler will return a result of `'r`. The types makes the
pipeline a bit more challenging to work with but makes it easier to stay within the pipeline for the full processing of
the request.

If you are using a fixed error type with your SDK you may pin the error type using shadow types to simplify the handlers
e.g:

```fs
type HttpFuncResult<'r> = Task<Result<Context<'r>, HandlerError<ResponseException>>>
type HttpFunc<'a, 'r> = Context<'a> -> HttpFuncResult<'r, ResponseException>
type NextFunc<'a, 'r> = HttpFunc<'a, 'r, ResponseException>
type HttpHandler<'a, 'b, 'r> = NextFunc<'b, 'r, ResponseException> -> Context<'a> -> HttpFuncResult<'r, ResponseException>
type HttpHandler<'a, 'r> = HttpHandler<'a, 'a, 'r, ResponseException>
type HttpHandler<'r> = HttpHandler<HttpResponseMessage, 'r, ResponseException>
type HttpHandler = HttpHandler<HttpResponseMessage, ResponseException>
```

## Using Together with Giraffe

You can use Oryx within your Giraffe server if you need to make HTTP requests to other services. But then you must be careful about the order when opening namespaces so you know if you use the `>=>` operator from Oryx or Giraffe. Usually this will not be a problem since the Giraffe `>=>` will be used within your e.g `WebApp.fs` or `Server.fs`, while the Oryx `>=>` will be used within the controller handler function itself e.g `Controllers/Index.fs`. Thus just make sure you open Oryx after Giraffe in the controller files.

```fs
open Giraffe
open Oryx
```

## Libraries using Oryx:

- [Cognite SDK .NET](https://github.com/cognitedata/cognite-sdk-dotnet)
- [oryx-netatmo](https://github.com/dbrattli/oryx-netatmo)

## Code of Conduct

This project follows https://www.contributor-covenant.org, see our [Code of Conduct](https://github.com/cognitedata/oryx/blob/master/CODE_OF_CONDUCT.md).

## License

Apache v2, see [LICENSE](https://github.com/cognitedata/oryx/blob/master/LICENSE).
