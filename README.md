# Oryx

Oryx is a .NET cross platform functional web request handler library for writing web client libraries in F#.

> An SDK for writing SDKs.

This library enables you to write (or generate) Web and REST clients and SDKs for various APIs. Thus Oryx is an SDK for writing SDKs.

Oryx is heavily inspired by [Giraffe](https://github.com/giraffe-fsharp/Giraffe) web framework, and applies the same kind of ideas to the client making the web requests.

## Fundamentals

The main building block in Oryx is the `Context` and the `HttpHandler`. The Context stores all the state needed for performing the request and any data received from the response:

```fs
type Context<'a> = {
    Request: HttpRequest
    Result: Result<'a, ResponseError>
}
```

The `HttpHandler` takes a `Context` (and a `NextHandler`) and returns a new `Context`.

```fs
type NextHandler<'a, 'b> = Context<'a> -> Async<Context<'b>>

type HttpHandler<'a, 'b, 'c> = NextHandler<'b, 'c> -> Context<'a> -> Async<Context<'c>>

// For convenience
type HttpHandler<'a, 'b> = HttpHandler<'a, 'a, 'b>
type HttpHandler<'a> = HttpHandler<HttpResponseMessage, 'a>
type HttpHandler = HttpHandler<HttpResponseMessage>
```

An `HttpHandler` is a simple function which takes two curried arguments, and `NextHandler` and a `Context`, and returns a `Context` (wrapped in a `Result` and `Async` workflow) when finished.

On a high level an `HttpHandler` function takes and returns a context object, which means every `HttpHandler` function has full control of the outgoing HttpRequest and the resulting response.

Each HttpHandler usually adds more info to the `HttpRequest` before passing it further down the pipeline by invoking the next `NextHandler` or short circuit the execution by returning a result of Result<'a, ResponseError>.

If an HttpHandler detects an error, then it can return Result.Error instead to fail the processing.

The easiest way to get your head around a Oryx `HttpHandler` is to think of it as a functional Web request processing pipeline. Each handler has the full `Context` at its disposal and can decide whether it wants to return `Ok Context`, `Error` or pass it on to the "next" `NextHandler`.

## Operators

The fact that everything is an `HttpHandler` makes it easy to compose handlers together.

Two `HttpHandler` functions may be composed together using Keisli compsition and the fish operator `>=>`.

```fs
let (>=>) a b = compose a b
```

THe `compose` function is the magic that sews it all togheter and explains how you can curry the `HttpHandler` to generate a new `NextHandler`.

```fs
let compose (first : HttpHandler<'a, 'b, 'd>) (second : HttpHandler<'b, 'c, 'd>) : HttpHandler<'a,'c,'d> =
    fun (next: NextHandler<_, _>) (ctx : Context<'a>) ->
        let next' = second next
        let next'' = first next'

        next'' ctx
```

This enables you to compose your web requests, e.g:

```fs
    GET
    >=> setVersion V10
    >=> addQuery query
    >=> setResource Url
    >=> fetch
    >=> decoder
```

## JSON and Protobuf

Oryx will serialize and deserialize JSON using `Thoth.Json.Net` or Protobuf using `Google.Protobuf`.

Both encode and decode uses streaming so no large strings or arrays will be allocated in the process.

## Computational Expression Builder

Working with `Context` objects can be a bit painful since the actual result will be available inside an `Async` effect that has a `Result` that can be either `Ok` with the response or `Error`. To make it simpler to handle multiple requests using handlers you can use the `oryx` builder that will hide the complexity of both the `Context` and the `Result`.

```fs
    oryx {
        let! data = fetchData ()

        return data
    }
```
