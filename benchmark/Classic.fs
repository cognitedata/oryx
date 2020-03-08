module Benchmark.Classic

open System
open System.Threading.Tasks
open System.Net.Http

open FSharp.Control.Tasks.V2.ContextInsensitive
open Thoth.Json.Net

open Oryx
open Oryx.ThothJsonNet
open Oryx.Context
open Common
open System.Threading

type HttpFuncResultClassic<'TResult, 'TError> =  Task<Result<Context<'TResult>, HandlerError<'TError>>>

type HttpFuncClassic<'T, 'TResult, 'TError> = Context<'T> -> HttpFuncResultClassic<'TResult, 'TError>

type HttpHandlerClassic<'T, 'TResult, 'TError> = Context<'T> -> HttpFuncResultClassic<'TResult, 'TError>

type HttpHandlerClassic<'TResult, 'TError> = HttpHandlerClassic<HttpResponseMessage, 'TResult, 'TError>

type HttpHandlerClassic<'TError> = HttpHandlerClassic<HttpResponseMessage, 'TError>

module ClassicHandler =
    /// Run the HTTP handler in the given context.
    let runAsync (handler: HttpHandlerClassic<'T,'TResult,'TError>) (ctx : Context<'T>) : Task<Result<'TResult, HandlerError<'TError>>> =
        task {
            let! result = handler ctx
            match result with
            | Ok a -> return Ok a.Response
            | Error err -> return Error err
        }

    let map (mapper: 'T1 -> 'T2) (ctx : Context<'T1>) : HttpFuncResultClassic<'T2, 'TError> =
        Ok { Request = ctx.Request; Response = (mapper ctx.Response) } |> Task.FromResult

    let inline compose (first : HttpHandlerClassic<'T, 'TNext, 'TError>) (second : HttpHandlerClassic<'TNext, 'TResult, 'TError>) : HttpHandlerClassic<'T, 'TResult, 'TError> =
        fun (ctx : Context<'T>) -> task {
            let! result = first ctx
            match result with
            | Ok ctx' -> return! second ctx'
            | Error err -> return Error err
        }

    let (>=>) = compose

    let GET<'TResult, 'TError> (context: HttpContext) =
        Ok { context with Request = { context.Request with Method = HttpMethod.Get; ContentBuilder = None } } |> Task.FromResult

    let withError<'TError> (errorHandler : HttpResponseMessage -> Task<HandlerError<'TError>>) (context: HttpContext) : HttpFuncResultClassic<HttpResponseMessage, 'TError> =
        task {
            let response = context.Response
            match response.IsSuccessStatusCode with
            | true -> return Ok context
            | false ->
                let! err = errorHandler response
                return err |> Error
        }

    let withUrlBuilder<'TResult, 'TError> (builder: UrlBuilder) (context: HttpContext) =
        Ok { context with Request = { context.Request with UrlBuilder = builder } } |> Task.FromResult

    let setUrl<'TResult, 'TError> (url: string) (context: HttpContext) =
        withUrlBuilder (fun _ -> url) context

    let fetch<'TError> (ctx: HttpContext) : HttpFuncResultClassic<HttpResponseMessage, 'TError> =
        let client = ctx.Request.HttpClient ()
        let cancellationToken = ctx.Request.CancellationToken

        task {
            try
                use message = buildRequest client ctx
                let! response = client.SendAsync(message, cancellationToken)
                return Ok { ctx with Response = response }
            with
            | ex -> return Panic ex |> Error
        }

    let json<'T, 'TError> (decoder : Decoder<'T>) (context: HttpContext) : HttpFuncResultClassic<'T, 'TError> =
        task {
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

    let noop (ctx: Context<'T>) : HttpFuncResultClassic<int, 'TError> =
        let ctx' = { Request = ctx.Request; Response = 42 }
        Ok ctx' |> Task.FromResult

    let get () =
        GET
        >=> setUrl "http://test"
        >=> fetch
        >=> withError decodeError
        >=> json decoder
        >=> noop
        >=> noop

 type RequestBuilder () =
    member this.Zero () : HttpHandlerClassic<HttpResponseMessage, _, 'TError> =
        fun _ ->
            Ok Context.defaultContext |> Task.FromResult

    member this.Return (res: 'T) : HttpHandlerClassic<HttpResponseMessage, 'T, 'TError> =
        fun _ ->
            Ok { Request = Context.defaultRequest; Response = res } |> Task.FromResult

    member this.Return (req: HttpRequest) : HttpHandlerClassic<HttpResponseMessage, _, 'TError> =
        fun _ ->
            Ok { Request = req; Response = Context.defaultResult } |> Task.FromResult

    member this.ReturnFrom (req : HttpHandlerClassic<'T, 'TResult, 'TError>) : HttpHandlerClassic<'T, 'TResult, 'TError> = req

    member this.Delay (fn) = fn ()

    member this.Bind(source: HttpHandlerClassic<'T, 'TNext, 'TError>, fn: 'TNext -> HttpHandlerClassic<'T, 'TResult, 'TError>) : HttpHandlerClassic<'T, 'TResult, 'TError> =
        fun ctx -> task {
            let! br = source ctx
            match br with
            | Ok cb ->
                let b = cb.Response
                return! (fn cb.Response) ctx
            | Error err -> return Error err
        }


module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder ()
