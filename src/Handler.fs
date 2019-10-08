// Copyright 2019 Cognite AS

namespace Oryx

open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks.V2.ContextInsensitive

type HttpFunc<'a, 'b> = Context<'a> -> Task<Context<'b>>

type NextFunc<'a, 'b> = HttpFunc<'a, 'b>

type HttpHandler<'a, 'b, 'c> = NextFunc<'b, 'c> -> Context<'a> -> Task<Context<'c>>

type HttpHandler<'a, 'b> = HttpHandler<'a, 'a, 'b>

type HttpHandler<'a> = HttpHandler<HttpResponseMessage, 'a>

type HttpHandler = HttpHandler<HttpResponseMessage>

[<AutoOpen>]
module Handler =

    /// Run the handler with the given context.
    let runHandler (handler: HttpHandler<'a,'b,'b>) (ctx : Context<'a>) : Task<Result<'b, ResponseError>> =
        task {
            let! a = handler Task.FromResult ctx
            return a.Result
        }
    let map (mapper: 'a -> 'b) (next : NextFunc<'b,'c>) (ctx : Context<'a>) : Task<Context<'c>> =
        match ctx.Result with
        | Ok value -> next { Request = ctx.Request; Result = Ok (mapper value) }
        | Error ex -> Task.FromResult { Request = ctx.Request; Result = Error ex }

    let compose (first : HttpHandler<'a, 'b, 'd>) (second : HttpHandler<'b, 'c, 'd>) : HttpHandler<'a,'c,'d> =
        fun (next: NextFunc<_, _>) (ctx : Context<'a>) ->
            let func =
                next
                |> second
                |> first

            func ctx

    let (>=>) a b =
        compose a b

    // https://fsharpforfunandprofit.com/posts/elevated-world-4/
    let traverseContext fn (list : Context<'a> list) =
        // define the monadic functions
        let (>>=) ctx fn = Context.bind fn ctx

        let retn a =
            { Request = Context.defaultRequest; Result = Ok a }

        // define a "cons" function
        let cons head tail = head :: tail

        // right fold over the list
        let initState = retn []
        let folder head tail =
            fn head >>= (fun h ->
                tail >>= (fun t ->
                    retn (cons h t)
                )
            )

        List.foldBack folder list initState

    let sequenceContext (ctx : Context<'a> list) : Context<'a list> = traverseContext id ctx

    /// Run list of HTTP handlers concurrently.
    let concurrent (handlers : HttpHandler<'a, 'b, 'b> seq) (next: NextFunc<'b list, 'c>) (ctx: Context<'a>) : Task<Context<'c>> = task {
        let! res =
            handlers
            |> Seq.map (fun handler -> handler Task.FromResult ctx)
            |> Task.WhenAll

        return! next (res |> List.ofArray |> sequenceContext)
    }

    /// Run list of HTTP handlers sequentially.
    let sequential (handlers : HttpHandler<'a, 'b, 'b> seq) (next: NextFunc<'b list, 'c>) (ctx: Context<'a>) : Task<Context<'c>> = task {
        let res = ResizeArray<Context<'b>>()

        for handler in handlers do
            let! result = handler Task.FromResult ctx
            res.Add result

        return! next (res |> List.ofSeq |> sequenceContext)
    }

    let extractHeader (header: string) (next: NextFunc<_,_>) (context: HttpContext) = task {
        match context.Result with
        | Ok response ->
            let (success, values) = response.Headers.TryGetValues header
            let values = if (isNull values) then [] else values |> List.ofSeq
            match (success, values ) with
            | (true, value :: _) ->
                return! next { Request = context.Request; Result = Ok value }
            | _ ->
                return { Request = context.Request; Result = Error { ResponseError.empty with Message = sprintf "Missing header: %s" header }}
        | Error error -> return { Request = context.Request; Result = Error error }
    }