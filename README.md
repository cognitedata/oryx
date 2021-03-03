# Oryx

![Build and Test](https://github.com/cognitedata/oryx/workflows/Build%20and%20Test/badge.svg)
[![codecov](https://codecov.io/gh/cognitedata/oryx/branch/master/graph/badge.svg)](https://codecov.io/gh/cognitedata/oryx)
[![Nuget](https://img.shields.io/nuget/vpre/oryx)](https://www.nuget.org/packages/Oryx/)

Oryx is a high-performance .NET cross-platform functional HTTP request handler library for writing HTTP clients and
orchestrating web requests in F#.

> An SDK for writing HTTP web clients and orchestrating web requests.

This library enables you to write Web and REST clients and SDKs for various APIs and is currently used by the [.NET SDK
for Cognite Data Fusion (CDF)](https://github.com/cognitedata/cognite-sdk-dotnet).

Oryx is heavily inspired by the [AsyncRx](https://github.com/dbrattli/AsyncRx) and
[Giraffe](https://github.com/giraffe-fsharp/Giraffe) frameworks and applies the same ideas to the client making the web
requests. You can think of Oryx as the client equivalent of Giraffe, where the HTTP request processing pipeline starting
at the client, going all the way to the server and back again.

## Installation

Oryx is available as a [NuGet package](https://www.nuget.org/packages/Oryx/). To install:

Using Package Manager:

```sh
Install-Package Oryx
```

Using .NET CLI:
```sh
dotnet add package Oryx
```

Or [directly in Visual Studio](https://docs.microsoft.com/en-us/nuget/quickstart/install-and-use-a-package-in-visual-studio).

## Getting Started

```fs
open System.Net.Http
open System.Text.Json

open FSharp.Control.Tasks

open Oryx
open Oryx.SystemTextJson.ResponseReader

[<Literal>]
let Url = "https://en.wikipedia.org/w/api.php"

let options = JsonSerializerOptions()

let query term = [
    struct ("action", "opensearch")
    struct ("search", term)
]

let request term =
    GET
    >=> withUrl Url
    >=> withQuery (query term)
    >=> fetch
    >=> json options

let asyncMain argv = task {
    use client = new HttpClient ()
    let ctx =
        Context.defaultContext
        |> Context.withHttpClient client

    let! result = request "F#" |> runAsync ctx
    printfn "Result: %A" result
}

[<EntryPoint>]
let main argv =
    asyncMain().GetAwaiter().GetResult()
    0 // return an integer exit code
```

## Fundamentals

The main building blocks in Oryx are the `Context` and the `HttpHandler`. The Context contains all the state needed for
making the request, and also contains any response metadata such as headers, response code etc received from the remote
server:

```fs
type Context = {
    Request: HttpRequest
    Response: HttpResponse
}
```

The `Context` is constructed using a series of context builder functions (`Context -> Context`). Request specific
changes to the context is done using a series of asynchronous HTTP handlers.

```fs
type IHttpNext<'TSource> =
    abstract member OnNextAsync: context: Context * ?content: 'TSource -> Task<unit>
    abstract member OnErrorAsync: context: Context * error: exn -> Task<unit>

type IHttpHandler<'TSource, 'TResult> =
    abstract member Subscribe: next: IHttpNext<'TResult> -> IHttpNext<'TSource>
```


The relationship can be seen as:
```fs
source = handler.Subscribe(result)
```

A handler (`IHttpHandler`) is an observable that subscribes `.Subscribe()`. the result observer (`IHttpNext<'TResult>`),
and also returns the source observer (`IHttpNext<'TSource>`). The returned `IHttpNext<'TSource>` is an observer used to
write the `Context` and an optional content (`'TSource`) into the handler. The given result observer
`IHttpNext<'Result>` is where the `HttpHandler` will write its output. You can think of the `IHttpNext` as the input and
output observers (or continuations) of the `HttpHandler`.

Each `IHttpHandler` usually transforms the `HttpRequest`, `HttpResponse` or the `content` before passing it down the
pipeline by invoking the next `IHttpNext`'s `.OnNextAsync()` member. It may also signal error by calling the
`OnErrorAsync` member to fail the processing of the pipeline.

The easiest way to get your head around a Oryx `IHttpHandler` is to think of it as a functional web request processing
pipeline. Each handler has the `Context` and `content` at its disposal and can decide whether it wants to fail the
request or continue the request by passing to the "next" handler.

## Context Builders

The context you want to use for your requests may constructed using a builder like pattern (`Context -> Context`) where
you set the common things you need for all of your requests. You create the context using synchronous functions where
you can set e.g. the headers you want to use, the HTTP client, URL builder, logging and metrics.

- `defaultContext` - A default empty context.

The following builder functions may be used:

- `withHeader` - Adds a header to the context.
- `withHeaders` - Adds headers to the context.
- `withBearerToken` - Adds an `Authorization` header with `Bearer` token.
- `withHttpClient` - Adds the `HttpClient` to use for making requests using the `fetch` handler.
- `withHttpClientFactory` - Adds an `HttpClient` factory function to use for producing the `HttpClient`.
- `withUrlBuilder` - Adds the URL builder to use. An URL builder construct the URL for the `Request` part of the
  context.
- `withCancellationToken` - Adds a cancellation token to use for the context. This is particularly useful when using
  Oryx together with C# client code that supplies a cancellation token.
- `withLogger` - Adds an
  [`ILogger`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.ilogger?view=dotnet-plat-ext-3.1)
  for logging requests and responses.
- `withLogLevel` - The log level
  ([`LogLevel`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.loglevel?view=dotnet-plat-ext-3.1))
  that the logging should be performed at. Oryx will disable logging for `LogLevel.None` and this is also the default
  log level.
- `withLogFormat` - Specify the log format of the log messages written.
- `withMetrics` - Add and `IMetrics` interface to produce metrics info.

## HTTP Handlers

The context and content may then be transformed for individual requests using a series of HTTP handlers. HTTP handlers
are like lego bricks and may be composed into more complex HTTP handlers. The HTTP handlers included with Oryx are:

- `catch` - Catches errors and continue using another handler.
- `choose` - Choose the first handler that succeeds in a list of handlers.
- `chunk` - Chunks a sequence of HTTP handlers into sequential and concurrent batches.
- `concurrent` - Runs a sequence of HTTP handlers concurrently.
- `fetch` - Fetches from remote using the current context
- `log` - Log information about the given request.
- `parse` - Parse response stream to a user-specified type synchronously.
- `parseAsync` - Parse response stream to a user-specified type asynchronously.
- `sequential` - Runs a sequence of HTTP handlers sequentially.
- `throw`- Fails the pipeline and pushes an exception downstream.
- `withContent` - Add HTTP content to the fetch request
- `withLogMessage` - Log information about the given request supplying a user-specified message.
- `withMethod` - with HTTP method. You can use GET, PUT, POST instead.
- `withQuery` - Add URL query parameters
- `withResponseType` - Sets the Accept header of the request.
- `withUrl` - Use the given URL for the request.
- `withUrlBuilder` - Use the given URL builder for the request.
- `withError` - Detect if the HTTP request failed, and then fail processing.
- `withTokenRenewer` - Enables refresh of bearer tokens without building a new context.

In addition there are several extension for decoding JSON and Protobuf responses:

- `json` - Decodes the given `application/json` response into a user-specified type.
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

The real magic of Oryx is composition. The fact that everything is an `IHttpHandler` makes it easy to compose HTTP
handlers together. You can think of them as lego bricks that you can fit together. Two or more `IHttpHandler` functions
may be composed together using Kleisli composition, i.e using the fish operator `>=>`. This enables you to compose your
web requests and decode the response, e.g as we do when listing Assets in the [Cognite Data Fusion
SDK](https://github.com/cognitedata/cognite-sdk-dotnet/blob/master/Oryx.Cognite/src/Handler.fs):

```fs
    let list (query: AssetQuery) : HttpHandler<unit, ItemsWithCursor<AssetReadDto>> =
        let url = Url +/ "list"

        POST
        >=> withVersion V10
        >=> withResource url
        >=> withContent (() -> new JsonPushStreamContent<AssetQuery>(query, jsonOptions))
        >=> fetch
        >=> withError decodeError
        >=> json jsonOptions
```

The function `listAssets` is now also an `IHttpHandler` and may be composed with other handlers to create complex chains
for doing multiple sequential or concurrent to a web service. And you can do this without having to worry about error
handling.

## Retrying Requests

Since Oryx is based on `HttpClient` from `System.Net.Http`, you may also use [Polly](https://github.com/App-vNext/Polly)
for handling resilience.

## Concurrent and Sequential Handlers

A `sequential` operator for running a list of HTTP handlers in sequence.

```fs
val sequential:
   handlers: seq<IHttpHandler<'TSource,'TResult>> ->
   next    : IHttpNext<list<'TResult>>
          -> IHttpNext<'TSource>
```

And a `concurrent` operator that runs a list of HTTP handlers in parallel.

```fs
val concurrent:
   handlers: seq<IHttpHandler<'TSource,'TResult>> ->
   next    : IHttpNext<list<'TResult>>
          -> IHttpNext<'TSource>
```

You can also combine sequential and concurrent requests by chunking the request. The `chunk` handler uses `chunkSize`
and `maxConcurrency` to decide how much will be done in parallel. It takes a list of items and a handler that transforms
these items into HTTP handlers. This is nice if you need to e.g read thousands of items from a web service in
multiple requests.

```fs
val chunk:
   chunkSize     : int         ->
   maxConcurrency: int         ->
   handler       : seq<'TNext> -> IHttpHandler<'TSource,seq<'TResult>> ->
   items         : seq<'TNext>
                -> IHttpHandler<'TSource,seq<'TResult>>

```

Note that chunk will fail if one of the inner requests fails so for e.g a writing scenario you most likely want to
create your own custom chunk operator that have different error semantics. If you write such operators then feel free to
open a PR so we can include them in the library.

## Error handling

Errors are handled by the main handler logic. Every `IHttpNext` has a member `OnErrorAsync` that takes the context and
an exception. Thus every stage in the pipeline may be short-circuited by calling the error handler.

To produce a custom error response you can use the `withError` handler _after_ e.g `fetch`. The supplied `errorHandler`
is given full access the the `HttpResponse` and the `HttpContent` and may produce a custom `exception`.

```fs
val withError:
   errorHandler: HttpResponse -> HttpContent option -> Task<exn> ->
   next        : IHttpNext<HttpContent>
              -> IHttpNext<HttpContent>
```

It's also possible to catch errors using the `catch` handler _after_ e.g `fetch`. The function takes an `errorHandler`
that is given the returned error and produces a new `HttpHandler` that may then decide to transform the error and
continue processing, or fail with an error. This is very helpful when a failed request not necessarily means an error,
e.g if you need to check if an object with a given id exists at the server.

```fs
val catch:
   errorHandler: exn -> IHttpHandler<'TSource> ->
   next        : IHttpNext<'TSource>
              -> IHttpNext<'TSource>
```

## JSON and Protobuf Content Handling

Oryx can serialize (and deserialize) content using:

- [`System.Text.Json`](https://docs.microsoft.com/en-us/dotnet/api/system.text.json?view=netcore-3.1)
- [`Newtonsoft.Json`](https://www.newtonsoft.com/json)
- [`Thoth.Json.Net`](https://github.com/thoth-org/Thoth.Json.Net)
- [`Google.Protobuf`](https://developers.google.com/protocol-buffers)

### System.Text.Json

Support for `System.Text.Json` is made available using the
[`Oryx.SystemTextJson`](https://www.nuget.org/packages/Oryx.SystemTextJson/) extension.

The `json` decode HTTP handler takes a `JsonSerializerOptions` to decode the response into user defined type of `'T`.

```fs
val json:
   options: JsonSerializerOptions
         -> HttpHandler<HttpContent,'TResult>
```

Content can be handled using `type JsonPushStreamContent<'a> (content : 'T, options : JsonSerializerOptions)`.

### Newtonsoft.Json

Support for `Newtonsoft.Json` is made available using the
[`Oryx.NewtonsoftJson`](https://www.nuget.org/packages/Oryx.NewtonsoftJson/) extension.

The `json` decode HTTP handler decodes the response into user defined type of `'TResult`.

```fs
val json : HttpHandler<HttpContent,'TResult>
```

Content can be handled using `type JsonPushStreamContent (content : JToken)`.

### Thoth.Json.Net

Support for `Thoth.Net.Json` is made available using the
[`Oryx.ThothNetJson`](https://www.nuget.org/packages/Oryx.Protobuf/) extension.

The `json` decoder takes a `Decoder` from `Thoth.Json.Net` to decode the response into user defined type of `'T`.

```fs
val json:
   decoder: Decoder<'TResult>
         -> IHttpHandler<HttpContent,'TResult>
```

Content can be handled using `type JsonPushStreamContent (content : JsonValue)`.

### Protobuf

Protobuf support is made available using the [`Oryx.Protobuf`](https://www.nuget.org/packages/Oryx.ThothJsonNet/)
extension.

The `protobuf` decoder takes a `Stream -> 'T` usually generated by ``. to decode the response into user defined type of `'T`.

```fs
val protobuf: (System.IO.Stream -> 'TResult) -> IHttpNext<'TResult> -> IHttpNext<System.Net.Http.HttpContent>
```

Both encode and decode uses streaming all the way so no large strings or arrays will be allocated in the process.

Content can be handled using `type ProtobufPushStreamContent (content : IMessage)`.

## Computational Expression Builder

Working with `Context` objects can be a bit painful since the actual result will be available inside a `Task` effect
that has a `Result` that can be either `Ok` of the actual response, or `Error`. To make it simpler to handle multiple
requests using handlers you can use the `req` builder that will let you work with the `content` and hide the complexity
of both the `Context` and the `IHttpNext`.

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
val runAsync:
   ctx    : Context ->
   handler: IHttpHandler<'T,'TResult>
         -> Task<Result<'TResult,exn>>
```

or the unsafe version that may throw exceptions:

```fs
val runUnsafeAsync:
   ctx    : Context ->
   handler: IHttpHandler<'T,'TResult>
         -> Task<'TResult>
```

## Logging and Metrics

Oryx supports logging using the logging handlers. To setup for logging you first need to enable logging in the context
by both setting a logger of type `ILogger`
([Microsoft.Extensions.Logging](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.ilogger?view=dotnet-plat-ext-3.1))
and the logging level to something higher than `LogLevel.None`.

```fs
val withLogger : (logger: ILogger) -> (context: EmptyContext) -> (context: EmptyContext)
val withLogLevel : (logLevel: LogLevel) -> (context: EmptyContext) -> (context: EmptyContext)
val withLogFormat (format: string) (context: EmptyContext) -> (context: EmptyContext)
```

The default format string is:

`"Oryx: {Message} {HttpMethod} {Uri}\n{RequestContent}\n{ResponseContent}"`

You can also use a custom log format string by setting the log format using `withLogFormat`. The available place holders
you may use are:

- `Elapsed` - The elapsed request time for `fetch` in milliseconds.
- `HttpMethod` - The HTTP method used, i.e `PUT`, `GET`, `POST`, `DELETE` or `PATCH`.
- `Message` - A user-supplied message using `logWithMessage`.
- `ResponseContent` - The response content received. Must implement `ToString` to give meaningful output.
- `RequestContent` - The request content being sent. Must implement `ToString` to give meaningful output.
- `Url` - The URL used for fetching.

**Note:** Oryx will not call `.ToString ()` but will hand it over to the `ILogger` for the actual string interpolation,
given that the message will actually end up being logged.

NOTE: The logging handler (`log`) do not alter the types of the pipeline and may be composed anywhere. But to give
meaningful output they should be composed after fetching (`fetch`) but before error handling (`withError`). This is
because fetch related values goes down the pipeline while error values short-circuits and goes up. So you need to log in
between to catch both.

```fs
val withLogger:
   logger: ILogger             ->
   next  : IHttpNext<'TSource>
        -> IHttpNext<'TSource>

val withLogLevel:
   logLevel: LogLevel            ->
   next    : IHttpNext<'TSource>
          -> IHttpNext<'TSource>

val withLogMessage:
   msg : string              ->
   next: IHttpNext<'TSource>
      -> IHttpNext<'TSource>

val withLogMessage:
   msg : string              ->
   next: IHttpNext<'TSource>
      -> IHttpNext<'TSource>
```

Oryx may also emit metrics using the `IMetrics` interface (Oryx specific) that you can use with e.g Prometheus.

```fs
type IMetrics =
    abstract member Counter : metric: string -> labels: IDictionary<string, string> -> increase: int64 -> unit
    abstract member Gauge : metric: string -> labels: IDictionary<string, string> -> value: float -> unit
```

The currently defined Metrics are:

- `Metric.FetchInc` - ("MetricFetchInc") The increase in the number of fetches when using the `fetch` handler.
- `Metric.FetchErrorInc` - ("MetricFetchErrorInc"). The increase in the number of fetch errors when using the `fetch`
  handler.
- `Metrics.FetchRetryInc` - ("MetricsFetchRetryInc"). The increase in the number of retries when using the `retry`
  handler.
- `Metric.FetchLatencyUpdate` - ("MetricFetchLatencyUpdate"). The update in fetch latency (in milliseconds) when using
  the `fetch` handler.
- `Metric.DecodeErrorInc` - ("Metric.DecodeErrorInc"). The increase in decode errors when using a `json` decode handler.

Labels are currently not set but are added for future use, e.g setting the error code for fetch errors etc.

## Extending Oryx

It's easy to extend Oryx with your own context builders and HTTP handlers. Everything is functions so you can easily add
your own context builders and HTTP handlers.

### Custom Context Builders

Custom context builders are just a function that takes a `Context` and returns a `Context`:

```fs
let withAppId (appId: string) (context: EmptyContext) =
    { context
        with Request = {
            context.Request with
                Headers = ("x-cdp-app", appId) :: context.Request.Headers
                Items = context.Request.Items.Add("hasAppId", String "true")
         }
    }
```

### Custom HTTP Handlers

Custom HTTP handlers may e.g populate the context, make asynchronous web requests and parse response content. HTTP
handlers are functions that takes an `IHttpNext<'TResult>`, and returns an `IHttpNext<'TSource>`. Example:

```fs
let withResource (resource: string): HttpHandler<'TSource> =
    { new IHttpHandler<'TSource, 'TResult> with
        member _.Subscribe(next) =
            { new IHttpNext<'TSource> with
                member _.OnNextAsync(ctx, ?content) =
                    next.OnNextAsync(
                        { ctx with
                            Request =
                                { ctx.Request with
                                    Items = ctx.Request.Items.Add("resource", String resource)
                                }
                        },
                        ?content = content
                    )

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
            } }
```

The handlers above will add custom values to the context that may be used by the supplied URL builder. Note that
anything added to the `Items` map is also available as place-holders in the logging format string.

```fs
let urlBuilder (request: HttpRequest) : string =
    let items = request.Items
    ...
```

## What is new in Oryx v3

Oryx v3 will significantly simplify the typing of Http handlers by:

1. Be based on Async Observables instead of result returning continuations. The result returning continuations were
   problematic in the sense that they both push values down in addition to returning (pulling) async values up, thus
   each HTTP handler needed to care about the input (`TSource`), output (`TNext`), final result (`TResult`) and error
   (`TError`) types. By never returning anything (`Task<unit>`) we get rid of the annoying return type.
2. Error type is now simply an exception.

This change effectively makes Oryx an Async Observable:

```fs
type IHttpNext<'TSource> =
    abstract member OnNextAsync: context: Context * ?content: 'TSource -> Task<unit>
    abstract member OnErrorAsync: context: Context * error: exn -> Task<unit>

type IHttpHandler<'TSource, 'TResult> =
    abstract member Subscribe: next: IHttpNext<'TResult> -> IHttpNext<'TSource>

type IHttpHandler<'TSource> = IHttpHandler<'TSource, 'TSource>
```

The difference from observables is that the `IHttpHandler` subscribe method returns another observer (`IHttpNext`)
instead of a `Disposable` and this observable is the side-effect that injects values into the pipeline (`Subject`). The
composition stays exactly the same so all HTTP pipelines will works as before. The typing just gets simpler to handle.

The custom error type (`TError`) has also been removed and we now use plain exceptions for all errors. Any custom error
types now needs to be an Exception subtype.

The `retry` operator has been deprecated. Use [Polly](https://github.com/App-vNext/Polly) instead. It might get back in
a later release but the observable pattern makes it hard to retry something upstream.

A `choose` operator have been added. This operator takes a list of HTTP handlers and tries each of them until one of
them succeeds.

## What is new in Oryx v2

We needed to change Oryx to preserve any response headers and status-code that got lost after decoding the response
content into a custom type. The response used to be a custom `'T` so it could not hold any additional info.
We changed this so the response is now an `HttpResponse` type:

```fs
type HttpResponse<'T> =
    {
        /// Response content
        Content: 'T
        /// Map of received headers
        Headers: Map<string, seq<string>>
        /// Http status code
        StatusCode: HttpStatusCode
        /// True if response is successful
        IsSuccessStatusCode: bool
        /// Reason phrase which typically is sent by servers together with the status code
        ReasonPhrase: string
    }

    /// Replaces the content of the HTTP response.
    member x.Replace<'TResult>(content: 'TResult): HttpResponse<'TResult> =
        {
            Content = content
            StatusCode = x.StatusCode
            IsSuccessStatusCode = x.IsSuccessStatusCode
            Headers = x.Headers
            ReasonPhrase = x.ReasonPhrase
        }

type Context<'T> =
    {
        Request: HttpRequest
        Response: HttpResponse<'T>
    }
```

## Upgrade from Oryx v2 to v3

Oryx v3 is mostly backwards compatible with v2. Your chains of operators will for most part look and work exactly the
same. There are however some notable changes:

- The `retry` operator has been deprecated for now. Use [Polly](https://github.com/App-vNext/Polly) instead.
- The `catch` operator needs to run __after__ the error producing operator e.g `fetch` (not before). This is because
  Oryx v3 pushes results "down" instead of returning them "up" the chain of operators. The good thing with this change
  is that a handler can now continue processing the rest of the pipeline after catching an error. This was not possible
  in v2 / v1 where the `catch` operator had to abort processing and produce a result.
- Http handlers take 2 generic types instead of 4. E.g `fetch<'TSource, 'TNext, 'TResult, 'TError>` now becomes
  `fetch<'TSource, 'TNext>` and the last two types can simply be removed from your code.
- `ResponseError` is gone. You need to sub-class an exception instead. This means that the `'TError' type is also gone
  from the handlers.
- Custom context builders do not need any changes
- Custom HTTP handlers must be refactored. Instead of returning a result (Ok/Error) the handler needs to push down the
  result either using the Ok path `next.OnNextAsync()` or fail with an error `next.OnErrorAsync()`. This is very similar
  to e.g Reactive Extensions (Rx) `OnNext` / `OnError`. E.g:

```fs
 let withResource (resource: string) (next: NextFunc<_,_>) (context: HttpContext) =
    next { context with Request = { context.Request with Items = context.Request.Items.Add(PlaceHolder.Resource, String resource) } }
```

Needs to be refactored to:

```fs
let withResource (resource: string): HttpHandler<'TSource> =
    { new IHttpHandler<'TSource, 'TResult> with
        member _.Subscribe(next) =
            { new IHttpNext<'TSource> with
                member _.OnNextAsync(ctx, ?content) =
                    next.OnNextAsync(
                        { ctx with
                            Request =
                                { ctx.Request with
                                    Items = ctx.Request.Items.Add(PlaceHolder.Resource, String resource)
                                }
                        },
                        ?content = content
                    )

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
            }}
```

It's a bit more verbose, but the inner part of the code is exactly the same.

## Upgrade from Oryx v1 to v2

The context is now initiated with a content `'T` of `unit`. E.g your custom HTTP handlers that is used before `fetch`
need to be rewritten from using a `'TSource` of `HttpResponseMessage` to `unit` e.g:

```diff
- let withLogMessage (msg: string) (next: HttpFunc<HttpResponseMessage, 'T, 'TError>) (context: EmptyContext) =
+ let withLogMessage (msg: string) (next: HttpFunc<unit, 'T, 'TError>) (context: EmptyContext) =
```

There is now also a `runAsync'` overload that returns the full `HttpResponse` record i.e:
`Task<Result<HttpResponse<'TResult>, HandlerError<'TError>>>`. This makes it possible to get the response status-code,
response-headers etc even after decoding of the content. This is great when using Oryx for a web-proxy or protocol
converter where you need to pass on any response-headers.

## Using Oryx with Giraffe

You can use Oryx within your Giraffe server if you need to make HTTP requests to other services. But then you must be
careful about the order when opening namespaces so you know if you use the `>=>` operator from Oryx or Giraffe. Usually
this will not be a problem since the Giraffe `>=>` will be used within your e.g `WebApp.fs` or `Server.fs`, while the
Oryx `>=>` will be used within the controller handler function itself e.g `Controllers/Index.fs`. Thus just make sure
you open Oryx after Giraffe in the controller files.

```fs
open Giraffe
open Oryx
```

## Libraries using Oryx:

- [Cognite SDK .NET](https://github.com/cognitedata/cognite-sdk-dotnet)
- [oryx-netatmo](https://github.com/dbrattli/oryx-netatmo) (Currently a bit outdated)

## Code of Conduct

This project follows https://www.contributor-covenant.org, see our [Code of Conduct](https://github.com/cognitedata/oryx/blob/master/CODE_OF_CONDUCT.md).

## License

Apache v2, see [LICENSE](https://github.com/cognitedata/oryx/blob/master/LICENSE).
