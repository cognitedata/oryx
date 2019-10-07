// Copyright 2019 Cognite AS

namespace Oryx

open System.Net.Http
open FSharp.Control.Tasks.V2.ContextInsensitive


type RequestBuilder () =
    member this.Zero () : HttpHandler<HttpResponseMessage, HttpResponseMessage, _> = fun next _ -> next Context.defaultContext
    member this.Return (res: 'a) : HttpHandler<HttpResponseMessage, 'a, _> = fun next _ ->  next { Request = Context.defaultRequest; Result = Ok res }

    member this.Return (req: HttpRequest) : HttpHandler<HttpResponseMessage, HttpResponseMessage, _> = fun next ctx -> next { Request = req; Result = Context.defaultResult }

    member this.ReturnFrom (req : HttpHandler<'a, 'b, 'c>)  : HttpHandler<'a, 'b, 'c> = req

    member this.Delay (fn) = fn ()

    member x.For(source:'a seq, func: 'a -> HttpHandler<'a, 'b, 'b>) : HttpHandler<'a, 'b list, 'c>  =
        source
        |> Seq.map (fun a -> (func a) )
        |> sequential

    member this.Bind(source: HttpHandler<'a, 'b, 'd>, fn: 'b -> HttpHandler<'a, 'c, 'd>) :  HttpHandler<'a, 'c, 'd> =
        fun (next : NextFunc<'c, 'd>) (ctx : Context<'a>) ->
            let next' (cb : Context<'b>) = task {
                match cb.Result with
                | Ok b ->
                    // Run function
                    return! (fn b) next ctx
                | Error error ->
                    return { Request = cb.Request; Result = Error error }
            }
            // Run source
            source next' ctx

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let oryx = RequestBuilder ()
