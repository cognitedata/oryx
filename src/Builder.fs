// Copyright 2019 Cognite AS

namespace Oryx

open System.Net.Http


type RequestBuilder () =
    member _.Zero () : HttpHandler<HttpResponseMessage, HttpResponseMessage, _, 'err> =
        fun next _ ->
            next Context.defaultContext

    member _.Return (res: 'T) : HttpHandler<HttpResponseMessage, 'T, _, 'TError> =
        fun next _ ->
            next { Request = Context.defaultRequest; Response = res }

    member _.Return (req: HttpRequest) : HttpHandler<HttpResponseMessage, HttpResponseMessage, _, 'err> =
        fun next _ ->
            next { Request = req; Response = Context.defaultResult }

    member _.ReturnFrom (req : HttpHandler<'T, 'TNext, 'TResult, 'TError>) : HttpHandler<'T, 'TNext, 'TResult, 'TError> = req

    member _.Delay (fn) = fn ()

    member _.For(source:'T seq, func: 'T -> HttpHandler<'T, 'TNext, 'TNext, 'TError>) : HttpHandler<'T, 'TNext list, 'TResult, 'TError> =
        source
        |> Seq.map func
        |> sequential

    /// Binds value of 'TValue for let! All handlers runs in same context within the builder.
    member _.Bind(source: HttpHandler<'T, 'TValue, 'TResult, 'TError>, fn: 'TValue -> HttpHandler<'T, 'TNext, 'TResult, 'TError>) : HttpHandler<'T, 'TNext, 'TResult, 'TError> =
        fun next ctx ->
            let next' (ctx' : Context<'TValue>) =
                fn ctx'.Response next ctx // Run function in context

            source next' ctx // Run source is context

[<AutoOpen>]
module Builder =
    /// Request builder for an async context of request/result
    let req = RequestBuilder ()
