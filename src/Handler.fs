// Copyright 2019 Cognite AS

namespace Oryx

open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks.V2.ContextInsensitive

type HttpFuncResult<'b> =  Task<Result<Context<'b>, ResponseError>>
type HttpFunc<'a, 'b> = Context<'a> -> HttpFuncResult<'b>

type NextFunc<'a, 'b> = HttpFunc<'a, 'b>

type HttpHandler<'a, 'b, 'c> = NextFunc<'b, 'c> -> Context<'a> -> HttpFuncResult<'c>

type HttpHandler<'a, 'b> = HttpHandler<'a, 'a, 'b>

type HttpHandler<'a> = HttpHandler<HttpResponseMessage, 'a>

type HttpHandler = HttpHandler<HttpResponseMessage>

[<AutoOpen>]
module Handler =
    let finishEarly<'a> : HttpFunc<'a, 'a> = Ok >> Task.FromResult

    /// Run the handler with the given context.
    let runHandler (handler: HttpHandler<'a,'b,'b>) (ctx : Context<'a>) : Task<Result<'b, ResponseError>> =
        task {
            let! result = handler finishEarly ctx
            match result with
            | Ok a -> return Ok a.Response
            | Error err -> return Error err
        }
    let map (mapper: 'a -> 'b) (next : NextFunc<'b,'c>) (ctx : Context<'a>) : Task<Result<Context<'c>, ResponseError>> =
        next { Request = ctx.Request; Response = (mapper ctx.Response) }

    let compose (first : HttpHandler<'a, 'b, 'd>) (second : HttpHandler<'b, 'c, 'd>) : HttpHandler<'a,'c,'d> =
        fun (next: NextFunc<_, _>) (ctx : Context<'a>) ->
            let func =
                next
                |> second
                |> first

            func ctx

    let (>=>) a b =
        compose a b

    /// Run list of HTTP handlers concurrently.
    let concurrent (handlers : HttpHandler<'a, 'b, 'b> seq) (next: NextFunc<'b list, 'c>) (ctx: Context<'a>) : HttpFuncResult<'c> = task {
        let! res =
            handlers
            |> Seq.map (fun handler -> handler finishEarly ctx)
            |> Task.WhenAll

        let result = res |> List.ofArray |> Result.sequenceList
        match result with
        | Ok results ->
            let bs = { Request = ctx.Request; Response = results |> List.map (fun r -> r.Response) }
            return! next bs
        | Error err -> return Error err
    }

    /// Run list of HTTP handlers sequentially.
    let sequential (handlers : HttpHandler<'a, 'b, 'b> seq) (next: NextFunc<'b list, 'c>) (ctx: Context<'a>) : HttpFuncResult<'c> = task {
        let res = ResizeArray<Result<Context<'b>, ResponseError>>()

        for handler in handlers do
            let! result = handler finishEarly ctx
            res.Add result

        let result = res |> List.ofSeq |> Result.sequenceList
        match result with
        | Ok results ->
            let bs = { Request = ctx.Request; Response = results |> List.map (fun c -> c.Response) }
            return! next bs
        | Error err -> return Error err
    }

    let extractHeader (header: string) (next: NextFunc<_,_>) (context: HttpContext) = task {
        let (success, values) = context.Response.Headers.TryGetValues header
        let values = if (isNull values) then [] else values |> List.ofSeq
        match (success, values ) with
        | (true, value :: _) ->
            return! next { Request = context.Request; Response = Ok value }
        | _ ->
            return Error { ResponseError.empty with Message = sprintf "Missing header: %s" header }
    }