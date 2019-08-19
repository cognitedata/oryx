# Oryx

![Nuget](https://img.shields.io/nuget/v/oryx)

Oryx is a high performance .NET cross platform functional HTTP request handler library for writing web client libraries in F#.

> An SDK for writing web client SDKs.

This library enables you to write (or generate) Web and REST clients and SDKs for various APIs. Thus Oryx is an SDK for writing SDKs.

You can think of Orix as tye client side Giraffe. Oryx is heavily inspired by the [Giraffe](https://github.com/giraffe-fsharp/Giraffe) web framework, and applies the same kind of ideas to the client making the web requests, as for the server processing them. Thus you could envision the processing pipeline starting at the client and going all the way to the server and back again.

## Fundamentals

The main building blocks in Oryx is the `Context` and the `HttpHandler`. The Context stores all the state needed for performing the request and any data received from the response:

```fs
type Context<'a> = {
    Request: HttpRequest
    Result: Result<'a, ResponseError>
}
```

The `Context` is transformed by HTTP handlers. The `HttpHandler` takes a `Context` (and a `NextFunc`) and returns a new `Context`.

```fs
type HttpFunc<'a, 'b> = Context<'a> -> Async<Context<'b>>
type NextFunc<'a, 'b> = HttpFunc<'a, 'b>

type HttpHandler<'a, 'b, 'c> = NextFunc<'b, 'c> -> Context<'a> -> Async<Context<'c>>

// For convenience
type HttpHandler<'a, 'b> = HttpHandler<'a, 'a, 'b>
type HttpHandler<'a> = HttpHandler<HttpResponseMessage, 'a>
type HttpHandler = HttpHandler<HttpResponseMessage>
```

An `HttpHandler` is a plain function that takes two curried arguments, and `NextFunc` and a `Context`, and returns a `Context` (wrapped in a `Result` and `Async`) when finished.

On a high level the `HttpHandler` function takes and returns a context object, which means every `HttpHandler` function has full control of the outgoing `HttpRequest` and also the resulting response.

Each HttpHandler usually adds more info to the `HttpRequest` before passing it further down the pipeline by invoking the next `NextFunc` or short circuit the execution by returning a result of `Result<'a, ResponseError>`.

If an HttpHandler detects an error, then it can return `Result.Error` to fail the processing.

The easiest way to get your head around a Oryx `HttpHandler` is to think of it as a functional Web request processing pipeline. Each handler has the full `Context` at its disposal and can decide whether it wants to return `Error` or pass on a new `Context` on to the "next" handler, `NextFunc`.

## Operators

The fact that everything is an `HttpHandler` makes it easy to compose handlers together. You can think of them as lego bricks that you can put together. Two `HttpHandler` functions may be composed together using Keisli compsition, i.e using the fish operator `>=>`.

```fs
let (>=>) a b = compose a b
```

THe `compose` function is the magic that sews it all togheter and explains how you can curry the `HttpHandler` to generate a new `NextFunc` that you give to next `HttpHandler`. If the first handler fails, the next handler will be skipped.

```fs
let compose (first : HttpHandler<'a, 'b, 'd>) (second : HttpHandler<'b, 'c, 'd>) : HttpHandler<'a,'c,'d> =
    fun (next: NextFunc<_, _>) (ctx : Context<'a>) ->
        let func =
            next
            |> second
            |> first

        func ctx
```

This enables you to compose your web requests and decode the response, e.g:

```fs
let listAssets (options: Option seq) (fetch: HttpHandler<HttpResponseMessage,Stream, 'a>) =
    let decoder = Encode.decodeResponse Assets.Decoder id
    let query = options |> Seq.map Option.Render |> List.ofSeq

    GET
    >=> setVersion V10
    >=> addQuery query
    >=> setResource Url
    >=> fetch
    >=> decoder
```

Thus the function `listAssets` is now also an `HttpHandler` and may be composed with other handlers to create complex chains for doing series of multiple requests to a web service.

There is also a `retry` that retries HTTP handlers using max number of retries and exponential backoff.

```fs
val retry : (initialDelay: int<ms>) -> (maxRetries: int) -> (handler: HttpHandler<'a,'b,'c>) -> (next: NextFunc<'b,'c>) -> (ctx: Context<'a>) -> Async<Context<'c>>
```

And a `concurrent` operator that runs a list of HTTP handlers in parallel.

```fs
val concurrent : (handlers: HttpHandler<'a, 'b, 'b> seq) -> (next: NextFunc<'b list, 'c>) -> (ctx: Context<'a>) -> Async<Context<'c>>
```

## JSON and Protobuf

Oryx will serialize (and deserialize) JSON using `Thoth.Json.Net` or Protobuf using `Google.Protobuf`.

Both encode and decode uses streaming so no large strings or arrays will be allocated in the process.

## Computational Expression Builder

Working with `Context` objects can be a bit painful since the actual result will be available inside an `Async` effect that has a `Result` that can be either `Ok` with the response or `Error`. To make it simpler to handle multiple requests using handlers you can use the `oryx` builder that will hide the complexity of both the `Context` and the `Result`.

```fs
    oryx {
        let! a = fetchData "service1"
        let! b = fetchData "service2"

        return a + b
    }
```

## TODO

- The library currently depends on [`Thoth.Json.Net`](https://mangelmaxime.github.io/Thoth/). This should at some point be split into a separate library.

- The library also assumes the type of the error response. This should perhaps be made more generic.