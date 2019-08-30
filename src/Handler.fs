// Copyright 2019 Cognite AS

namespace Oryx

open System.Net.Http

type HttpFunc<'a, 'b> = Context<'a> -> Async<Context<'b>>

type NextFunc<'a, 'b> = HttpFunc<'a, 'b>

type HttpHandler<'a, 'b, 'c> = NextFunc<'b, 'c> -> Context<'a> -> Async<Context<'c>>

type HttpHandler<'a, 'b> = HttpHandler<'a, 'a, 'b>

type HttpHandler<'a> = HttpHandler<HttpResponseMessage, 'a>

type HttpHandler = HttpHandler<HttpResponseMessage>

[<AutoOpen>]
module Handler =

    /// Run the handler with the given context.
    let runHandler (handler: HttpHandler<'a,'b,'b>) (ctx : Context<'a>) : Async<Result<'b, ResponseError>> =
        async {
            let! a = handler Async.single ctx
            return a.Result
        }
    let map (mapper) (next : NextFunc<'a list,'b>) (ctx : Context<'a list list>) : Async<Context<'b>> =
        match ctx.Result with
        | Ok value ->
            next { Request = ctx.Request; Result = Ok (mapper value) }
        | Error ex -> Async.single  { Request = ctx.Request; Result = Error ex }

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
    let concurrent (handlers : HttpHandler<'a, 'b, 'b> seq) (next: NextFunc<'b list, 'c>) (ctx: Context<'a>) : Async<Context<'c>> = async {
        let! res =
            handlers
            |> Seq.map (fun handler -> handler Async.single ctx)
            |> Async.Parallel
            |> Async.map List.ofArray

        return! next (res |> sequenceContext)
    }

    /// Run list of HTTP handlers sequentially.
    let sequential (handlers : HttpHandler<'a, 'b, 'b> seq) (next: NextFunc<'b list, 'c>) (ctx: Context<'a>) : Async<Context<'c>> = async {
        let! res =
            handlers
            |> Seq.map (fun handler -> handler Async.single ctx)
            |> Async.Sequential
            |> Async.map List.ofArray

        return! next (res |> sequenceContext)
    }
