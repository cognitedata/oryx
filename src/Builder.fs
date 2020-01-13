// Copyright 2019 Cognite AS

namespace Oryx

open System.Net.Http


type RequestBuilder () =
    member this.Zero () : HttpHandler<HttpResponseMessage, HttpResponseMessage, _, 'err> =
        fun next _ ->
            next Context.defaultContext

    member this.Return (res: 'a) : HttpHandler<HttpResponseMessage, 'a, _, 'err> =
        fun next _ ->
            next { Request = Context.defaultRequest; Response = res }

    member this.Return (req: HttpRequest) : HttpHandler<HttpResponseMessage, HttpResponseMessage, _, 'err> =
        fun next _ ->
            next { Request = req; Response = Context.defaultResult }

    member this.ReturnFrom (req : HttpHandler<'a, 'b, 'r, 'err>) : HttpHandler<'a, 'b, 'r, 'err> = req

    member this.Delay (fn) = fn ()

    member x.For(source:'a seq, func: 'a -> HttpHandler<'a, 'b, 'b, 'err>) : HttpHandler<'a, 'b list, 'r, 'err> =
        source
        |> Seq.map (fun a -> (func a) )
        |> sequential

    member this.Bind(source: HttpHandler<'a, 'b, 'r, 'err>, fn: 'b -> HttpHandler<'a, 'c, 'r, 'err>) : HttpHandler<'a, 'c, 'r, 'err> =
        fun next ctx ->
            let next' (cb : Context<'b>) =
                fn cb.Response next ctx // Run function in given context

            source next' ctx // Run source is given context

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder ()
