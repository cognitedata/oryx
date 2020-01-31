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

type HttpFuncResultClassic<'r, 'err> =  Task<Result<Context<'r>, HandlerError<'err>>>

type HttpFuncClassic<'a, 'r, 'err> = Context<'a> -> HttpFuncResultClassic<'r, 'err>

type HttpHandlerClassic<'a, 'r, 'err> = Context<'a> -> HttpFuncResultClassic<'r, 'err>

type HttpHandlerClassic<'r, 'err> = HttpHandlerClassic<HttpResponseMessage, 'r, 'err>

type HttpHandlerClassic<'err> = HttpHandlerClassic<HttpResponseMessage, 'err>

module ClassicHandler =
    /// Run the HTTP handler in the given context.
    let runAsync (handler: HttpHandlerClassic<'a,'r,'err>) (ctx : Context<'a>) : Task<Result<'r, HandlerError<'err>>> =
        task {
            let! result = handler ctx
            match result with
            | Ok a -> return Ok a.Response
            | Error err -> return Error err
        }

    let map (mapper: 'a -> 'b) (ctx : Context<'a>) : HttpFuncResultClassic<'b, 'err> =
        Ok { Request = ctx.Request; Response = (mapper ctx.Response) } |> Task.FromResult

    let inline compose (first : HttpHandlerClassic<'a, 'b, 'err>) (second : HttpHandlerClassic<'b, 'r, 'err>) : HttpHandlerClassic<'a, 'r, 'err> =
        fun (ctx : Context<'a>) -> task {
            let! result = first ctx
            match result with
            | Ok ctx' -> return! second ctx'
            | Error err -> return Error err
        }

    let (>=>) = compose

    let GET<'r, 'err> (context: HttpContext) =
        Ok { context with Request = { context.Request with Method = HttpMethod.Get; Content = nullContent } } |> Task.FromResult

    let withError<'err> (errorHandler : HttpResponseMessage -> Task<HandlerError<'err>>) (context: HttpContext) : HttpFuncResultClassic<HttpResponseMessage, 'err> =
        task {
            let response = context.Response
            match response.IsSuccessStatusCode with
            | true -> return Ok context
            | false ->
                let! err = errorHandler response
                return err |> Error
        }

    let setUrlBuilder<'r, 'err> (builder: UrlBuilder) (context: HttpContext) =
        Ok { context with Request = { context.Request with UrlBuilder = builder } } |> Task.FromResult

    let setUrl<'r, 'err> (url: string) (context: HttpContext) =
        setUrlBuilder (fun _ -> url) context

    let fetch<'err> (ctx: HttpContext) : HttpFuncResultClassic<HttpResponseMessage, 'err> =
        let client =
            match ctx.Request.HttpClient with
            | Some client -> client
            | None -> failwith "Must set httpClient"

        use source = new CancellationTokenSource()
        let cancellationToken =
            match ctx.Request.CancellationToken with
            | Some token -> token
            | None -> source.Token

        task {
            try
                use message = buildRequest client ctx
                let! response = client.SendAsync(message, cancellationToken)
                return Ok { ctx with Response = response }
            with
            | ex -> return Panic ex |> Error
        }

    let json<'a, 'err> (decoder : Decoder<'a>) (context: HttpContext) : HttpFuncResultClassic<'a, 'err> =
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

    let noop (ctx: Context<'a>) : HttpFuncResultClassic<int, 'err> =
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
    member this.Zero () : HttpHandlerClassic<HttpResponseMessage, _, 'err> =
        fun _ ->
            Ok Context.defaultContext |> Task.FromResult

    member this.Return (res: 'a) : HttpHandlerClassic<HttpResponseMessage, 'a, 'err> =
        fun _ ->
            Ok { Request = Context.defaultRequest; Response = res } |> Task.FromResult

    member this.Return (req: HttpRequest) : HttpHandlerClassic<HttpResponseMessage, _, 'err> =
        fun _ ->
            Ok { Request = req; Response = Context.defaultResult } |> Task.FromResult

    member this.ReturnFrom (req : HttpHandlerClassic<'a, 'r, 'err>) : HttpHandlerClassic<'a, 'r, 'err> = req

    member this.Delay (fn) = fn ()

    member this.Bind(source: HttpHandlerClassic<'a, 'b, 'err>, fn: 'b -> HttpHandlerClassic<'a, 'r, 'err>) : HttpHandlerClassic<'a, 'r, 'err> =
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
