module Benchmark.Ply

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.Builders
open FSharp.Control.Tasks.Builders.Unsafe
open Thoth.Json.Net

open Oryx
open Oryx.ThothJsonNet
open Oryx.Context
open Common

type HttpFuncResultPly<'TResult, 'TError> =  Ply.Ply<Result<Context<'TResult>, HandlerError<'TError>>>

type HttpFuncPly<'T, 'TResult, 'TError> = Context<'T> -> HttpFuncResultPly<'TResult, 'TError>

type HttpHandlerPly<'T, 'TResult, 'TError> = Context<'T> -> HttpFuncResultPly<'TResult, 'TError>

type HttpHandlerPly<'TResult, 'TError> = HttpHandlerPly<HttpResponseMessage, 'TResult, 'TError>

type HttpHandlerPly<'TError> = HttpHandlerPly<HttpResponseMessage, 'TError>

module Handler =
    /// Run the HTTP handler in the given context.
    let runAsync (handler: HttpHandlerPly<'T,'TResult,'TError>) (ctx : Context<'T>) : Ply.Ply<Result<'TResult, HandlerError<'TError>>> =
        uply {
            let! result = handler ctx
            match result with
            | Ok a -> return Ok a.Response
            | Error err -> return Error err
        }

    let map (mapper: 'T1 -> 'T2) (ctx : Context<'T1>) : HttpFuncResultPly<'T2, 'TError> =
        uply {
            return Ok { Request = ctx.Request; Response = (mapper ctx.Response) }
        }
    let inline compose (first : HttpHandlerPly<'T, 'TNext, 'TError>) (second : HttpHandlerPly<'TNext, 'TResult, 'TError>) : HttpHandlerPly<'T, 'TResult, 'TError> =
        fun (ctx : Context<'T>) -> uply {
            let! result = first ctx
            match result with
            | Ok ctx' -> return! second ctx'
            | Error err -> return Error err
        }

    let (>=>) = compose

    let GET<'TResult, 'TError> (context: HttpContext) =
        uply {
            return Ok { context with Request = { context.Request with Method = HttpMethod.Get; ContentBuilder = None } }
        }
    let withError<'TError> (errorHandler : HttpResponseMessage -> Task<HandlerError<'TError>>) (context: HttpContext) : HttpFuncResultPly<HttpResponseMessage, 'TError> =
        uply {
            let response = context.Response
            match response.IsSuccessStatusCode with
            | true -> return Ok context
            | false ->
                let! err = errorHandler response
                return err |> Error
        }

    let withUrlBuilder<'TResult, 'TError> (builder: UrlBuilder) (context: HttpContext) =
        uply {
            return Ok { context with Request = { context.Request with UrlBuilder = builder } }
        }

    let withUrl<'TResult, 'TError> (url: string) (context: HttpContext) =
        withUrlBuilder (fun _ -> url) context

    let fetch<'TError> (ctx: HttpContext) : HttpFuncResultPly<HttpResponseMessage, 'TError> =
        let client = ctx.Request.HttpClient ()
        let cancellationToken = ctx.Request.CancellationToken

        uply {
            try
                use message = buildRequest client ctx
                let! response = client.SendAsync(message, cancellationToken)
                return Ok { ctx with Response = response }
            with
            | ex -> return Panic ex |> Error
        }

    let json<'T, 'TError> (decoder : Decoder<'T>) (context: HttpContext) : HttpFuncResultPly<'T, 'TError> =
        uply {
            use response = context.Response
            let! stream = response.Content.ReadAsStreamAsync ()
            let! ret = decodeStreamAsync decoder stream
            do! stream.DisposeAsync ()
            match ret with
            | Ok result ->
                return Ok { Request = context.Request; Response = result }
            | Error error ->
                return Error (Panic <| JsonDecodeException error)
        }

    let decoder : Decoder<TestType> =
        Decode.object (fun get -> {
            Name = get.Required.Field "name" Decode.string
            Value = get.Required.Field "value" Decode.int
        })

    let noop (ctx: Context<'T>) : HttpFuncResultPly<int, 'TError> =
        let ctx' = { Request = ctx.Request; Response = 42 }
        uply {
            return Ok ctx'
        }

    let get () =
        GET
        >=> withUrl "http://test"
        >=> fetch
        >=> withError decodeError
        >=> json decoder
        >=> noop
        >=> noop

type RequestBuilder () =
    member this.Zero () : HttpHandlerPly<HttpResponseMessage, _, 'TError> =
        fun _ -> uply {
            return Ok Context.defaultContext
        }
    member this.Return (res: 'T) : HttpHandlerPly<HttpResponseMessage, 'T, 'TError> =
        fun _ -> uply {
            return Ok { Request = Context.defaultRequest; Response = res }
        }

    member this.Return (req: HttpRequest) : HttpHandlerPly<HttpResponseMessage, _, 'TError> =
        fun _ -> uply {
            return Ok { Request = req; Response = Context.defaultResult }
        }
    member this.ReturnFrom (req : HttpHandlerPly<'T, 'TResult, 'TError>) : HttpHandlerPly<'T, 'TResult, 'TError> = req

    member this.Delay (fn) = fn ()

    member this.Bind(source: HttpHandlerPly<'T, 'TNext, 'TError>, fn: 'TNext -> HttpHandlerPly<'T, 'TResult, 'TError>) : HttpHandlerPly<'T, 'TResult, 'TError> =
        fun ctx -> uply {
            let! br = source ctx
            match br with
            | Ok cb ->
                let b = cb.Response
                return! (fn cb.Response) ctx
            | Error err -> return Error err
        }

module Builder =
    /// Request builder for an async context of request/result
    let oryx = RequestBuilder ()
