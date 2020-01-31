# Oryx

[![Build](https://img.shields.io/travis/cognitedata/oryx)](https://travis-ci.org/cognitedata/oryx)
[![codecov](https://codecov.io/gh/cognitedata/oryx/branch/master/graph/badge.svg)](https://codecov.io/gh/cognitedata/oryx)
[![Nuget](https://img.shields.io/nuget/vpre/oryx)](https://www.nuget.org/packages/Oryx/)

Oryx is a high performance .NET cross platform functional HTTP request handler library for writing HTTP clients and orchestrating web requests in F#.

> An SDK for writing HTTP web clients or SDKs.

This library enables you to write Web and REST clients and SDKs for various APIs and is currently used by the [.NET SDK for Cognite Data Fusion (CDF)](https://github.com/cognitedata/cognite-sdk-dotnet).

Oryx is heavily inspired by the [Giraffe](https://github.com/giraffe-fsharp/Giraffe) web framework, and applies the same ideas to the client making the web requests. You can think of Oryx as the client equivalent of Giraffe, and you could envision the HTTP request processing pipeline starting at the client and going all the way to the server and back again.

## Fundamentals

The main building blocks in Oryx is the `Context` and the `HttpHandler`. The Context stores all the state needed for making the request and any response or error received from the server:

```fs
type Context<'a> = {
    Request: HttpRequest
    Response: 'a
}
```

The `Context` may be transformed by series of HTTP handlers. The `HttpHandler` takes a `Context` (and a `NextFunc`) and returns a new `Context` wrapped in a `Result` and `Task`.

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

An `HttpHandler` is a plain function that takes two curried arguments, a `NextFunc` and a `Context`, and returns a new `Context` (wrapped in a `Result` and `Task`) when finished. On a high level the `HttpHandler` function takes and returns a context object, which means every `HttpHandler` function has full control of the outgoing `HttpRequest` and also the resulting response.

Each HttpHandler usually adds more info to the `HttpRequest` before passing it further down the pipeline by invoking the next `NextFunc` or short circuit the execution by returning a result of `Result<Context<'a>, ResponseError>`. E.g if an HttpHandler detects an error, then it can return `Result.Error` to fail the processing.

The easiest way to get your head around a Oryx `HttpHandler` is to think of it as a functional Web request processing pipeline. Each handler has the full `Context` at its disposal and can decide whether it wants to fail the request by returning an `Error`, or continue the request by passing on a new `Context` to the "next" handler, `NextFunc`.

The more complex way to think about a `HttpHandler` is that there are in fact 3 different ways it may process the request:

1. Call the next handler with a result value (`'a`), and return what the next handler is returning.
2. Return an `Error`result to fail the request.
3. Return `Ok` to short circuit the processing. This is not something you would normally do.

## Operators

The fact that everything is an `HttpHandler` makes it easy to compose handlers together. You can think of them as lego bricks that you can fit together. Two `HttpHandler` functions may be composed together using Kleisli composition, i.e using the fish operator `>=>`.

```fs
let (>=>) a b = compose a b
```

The `compose` function is the magic that sews it all togheter and explains how we curry the `HttpHandler` to generate a new `NextFunc` that we give to next `HttpHandler`.

```fs
    let compose (first : HttpHandler<'a, 'b, 'r, 'err>) (second : HttpHandler<'b, 'c, 'r, 'err>) : HttpHandler<'a,'c,'r, 'err> =
        fun (next: NextFunc<'c, 'r, 'err>) (ctx : Context<'a>) ->
            let func =
                next
                |> second
                |> first

            func ctx

```
It turns out that we can simplify this even further using [η-conversion](https://wiki.haskell.org/Eta_conversion). Thus dropping `ctx` and eventually `next` as well:

```fs
    let compose = second >> first

    let (>=>) = compose
```

This enables you to compose your web requests and decode the response, e.g as we do when listing Assets in the [Cognite Data Fusion SDK](https://github.com/cognitedata/cognite-sdk-dotnet/blob/master/src/assets/ListAssets.fs#L55):

```fs
    let list (query: AssetQuery) : HttpHandler<HttpResponseMessage, ItemsWithCursor<AssetReadDto>, 'a> =
        let url = Url +/ "list"

        POST
        >=> setVersion V10
        >=> setResource url
        >=> setContent (new JsonPushStreamContent<AssetQuery>(query, jsonOptions))
        >=> fetch
        >=> withError decodeError
        >=> json jsonOptions
```

Thus the function `listAssets` is now also an `HttpHandler` and may be composed with other handlers to create complex chains for doing series of multiple requests to a web service.

There is also a `retry` that retries the `next` HTTP handler using max number of retries and exponential backoff.

```fs
val retry : (initialDelay: int<ms>) -> (maxRetries: int) -> (next: NextFunc<'b,'c>) -> (ctx: Context<'a>) -> Task<Context<'c>>
```

A `sequential` operator for running a list of HTTP handlers in sequence.

```fs
val sequential : (handlers : HttpHandler<'a, 'b, 'b, 'err> seq) -> (next: NextFunc<'b list, 'r, 'err>) -> (ctx: Context<'a>) -> HttpFuncResult<'r, 'err>
```

And a `concurrent` operator that runs a list of HTTP handlers in parallel.

```fs
val concurrent : (handlers: HttpHandler<'a, 'b, 'b> seq) -> (next: NextFunc<'b list, 'c>) -> (ctx: Context<'a>) -> Task<Context<'c>>
```

You can also combine sequential and concurrent requests by chunking the request. The `chunk` handler uses `chunkSize` and `maxConcurrency` to decide how much will be done in parallell. It takes a list of items and a handler that transforms these items into HTTP handlers. This is really nice if you need to e.g write thousands of items to a service in multiple requests.

```fs
val chunk<'a, 'b, 'r, 'err> : (chunkSize: int) -> (maxConcurrency: int) -> (handler: 'a seq -> HttpHandler<HttpResponseMessage, 'b seq, 'b seq, 'err>) -> (items: 'a seq) -> HttpHandler<HttpResponseMessage, 'b seq, 'r, 'err>
```

## Error handling

Errors are handled by the main handler logic. Every HTTP handler returns `Task<Result<Context<'r>, HandlerError<'err>>>`. Thus every stage in the pipeline may be short-circuit by `Error`, or be continued by `Ok`. The error type is generic and needs to be set by the client SDK or application. Oryx don't know anything about how to decode the `ResponseError`.

```fs
type HandlerError<'err> =
    /// Request failed with some exception, e.g HttpClient throws an exception, or JSON decode error.
    | Panic of exn
    /// User defined error response, e.g decoded error response from the API service.
    | ResponseError of 'err
```

To produce a custom error response you can use the `withError` handler _after_ e.g `fetch`. The supplied `errorHandler` is given full access the the `HttpResponseMessage` and may produce a custom `ErrorRespose`, or fail with `Panic` if decoding fails.

```fs
val withError<'a, 'r, 'err> (errorHandler : HttpResponseMessage -> Task<HandlerError<'err>>) -> (next: NextFunc<HttpResponseMessage,'r, 'err>) -> (context: HttpContext) -> HttpFuncResult<'r, 'err>
```

It's also possible to catch errors using the `catch` handler _before_ e.g `fetch`. The function takes an `errorHandler` that is given the returned error and produces a new `next` continuation that may then decide to return `Ok` instead of `Error`. This is very helpful when a failed request not necessarily means error, e.g if you need to check if an object with a given id exist at the server.

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

Working with `Context` objects can be a bit painful since the actual result will be available inside a `Task` effect that has a `Result` that can be either `Ok` of the actual response, or `Error`. To make it simpler to handle multiple requests using handlers you can use the `req` builder that will hide the complexity of both the `Context` and the `Result`.

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

To run a handler you can use the `runAsync` function.

```fs
let runAsync (handler: HttpHandler<'a,'r,'r, 'err>) (ctx : Context<'a>) : Task<Result<'r, HandlerError<'err>>>
```

## Differences from Giraffe

Oryx and Giraffe is very similar, but the model is a bit different. Oryx is for clients, Giraffe is for servers. In addition:

The Oryx `HttpHandler` is generic both on the response and error types. This means that you may decode the response or the error response to user defined types within the pipeline itself.

```fs
type HttpHandler<'a, 'b, 'r, 'err> = NextFunc<'b, 'r, 'err> -> Context<'a> -> HttpFuncResult<'r, 'err>
````

So an `HttpHandler` takes a context of `'a`. The handler itself transforms the context from `'a` to `'b`. Then the next handler continuation transforms from `'b` to `'r`, and the handler will return a result of `'r`. The types makes the pipeline a bit more challenging to work with but makes it easier to stay within the pipeline for the full processing of the request.

## Code of Conduct

This project follows https://www.contributor-covenant.org, see our [Code of Conduct](https://github.com/cognitedata/oryx/blob/master/CODE_OF_CONDUCT.md).

## License

Apache v2, see [LICENSE](https://github.com/cognitedata/oryx/blob/master/LICENSE).
