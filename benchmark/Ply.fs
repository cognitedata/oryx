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

type HttpFuncResultPly<'r, 'err> =  Ply.Ply<Result<Context<'r>, HandlerError<'err>>>

type HttpFuncPly<'a, 'r, 'err> = Context<'a> -> HttpFuncResultPly<'r, 'err>

type HttpHandlerPly<'a, 'r, 'err> = Context<'a> -> HttpFuncResultPly<'r, 'err>

type HttpHandlerPly<'r, 'err> = HttpHandlerPly<HttpResponseMessage, 'r, 'err>

type HttpHandlerPly<'err> = HttpHandlerPly<HttpResponseMessage, 'err>

module Handler =
    /// Run the HTTP handler in the given context.
    let runAsync (handler: HttpHandlerPly<'a,'r,'err>) (ctx : Context<'a>) : Ply.Ply<Result<'r, HandlerError<'err>>> =
        uply {
            let! result = handler ctx
            match result with
            | Ok a -> return Ok a.Response
            | Error err -> return Error err
        }

    let map (mapper: 'a -> 'b) (ctx : Context<'a>) : HttpFuncResultPly<'b, 'err> =
        uply {
            return Ok { Request = ctx.Request; Response = (mapper ctx.Response) }
        }
    let inline compose (first : HttpHandlerPly<'a, 'b, 'err>) (second : HttpHandlerPly<'b, 'r, 'err>) : HttpHandlerPly<'a, 'r, 'err> =
        fun (ctx : Context<'a>) -> uply {
            let! result = first ctx
            match result with
            | Ok ctx' -> return! second ctx'
            | Error err -> return Error err
        }

    let (>=>) = compose

    let GET<'r, 'err> (context: HttpContext) =
        uply {
            return Ok { context with Request = { context.Request with Method = HttpMethod.Get; Content = nullContent } }
        }
    let withError<'err> (errorHandler : HttpResponseMessage -> Task<HandlerError<'err>>) (context: HttpContext) : HttpFuncResultPly<HttpResponseMessage, 'err> =
        uply {
            let response = context.Response
            match response.IsSuccessStatusCode with
            | true -> return Ok context
            | false ->
                let! err = errorHandler response
                return err |> Error
        }

    let setUrlBuilder<'r, 'err> (builder: UrlBuilder) (context: HttpContext) =
        uply {
            return Ok { context with Request = { context.Request with UrlBuilder = builder } }
        }

    let setUrl<'r, 'err> (url: string) (context: HttpContext) =
        setUrlBuilder (fun _ -> url) context

    let fetch<'err> (ctx: HttpContext) : HttpFuncResultPly<HttpResponseMessage, 'err> =
        let client =
            match ctx.Request.HttpClient with
            | Some client -> client
            | None -> failwith "Must set httpClient"

        use source = new CancellationTokenSource()
        let cancellationToken =
            match ctx.Request.CancellationToken with
            | Some token -> token
            | None -> source.Token

        uply {
            try
                use message = buildRequest client ctx
                let! response = client.SendAsync(message, cancellationToken)
                return Ok { ctx with Response = response }
            with
            | ex -> return Panic ex |> Error
        }

    let json<'a, 'err> (decoder : Decoder<'a>) (context: HttpContext) : HttpFuncResultPly<'a, 'err> =
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

    let noop (ctx: Context<'a>) : HttpFuncResultPly<int, 'err> =
        let ctx' = { Request = ctx.Request; Response = 42 }
        uply {
            return Ok ctx'
        }

    let get () =
        GET
        >=> setUrl "http://test"
        >=> fetch
        >=> withError decodeError
        >=> json decoder
        >=> noop
        >=> noop

type RequestBuilder () =
    member this.Zero () : HttpHandlerPly<HttpResponseMessage, _, 'err> =
        fun _ -> uply {
            return Ok Context.defaultContext
        }
    member this.Return (res: 'a) : HttpHandlerPly<HttpResponseMessage, 'a, 'err> =
        fun _ -> uply {
            return Ok { Request = Context.defaultRequest; Response = res }
        }

    member this.Return (req: HttpRequest) : HttpHandlerPly<HttpResponseMessage, _, 'err> =
        fun _ -> uply {
            return Ok { Request = req; Response = Context.defaultResult }
        }
    member this.ReturnFrom (req : HttpHandlerPly<'a, 'r, 'err>) : HttpHandlerPly<'a, 'r, 'err> = req

    member this.Delay (fn) = fn ()

    member this.Bind(source: HttpHandlerPly<'a, 'b, 'err>, fn: 'b -> HttpHandlerPly<'a, 'r, 'err>) : HttpHandlerPly<'a, 'r, 'err> =
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
