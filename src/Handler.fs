// Copyright 2019 Cognite AS

namespace Oryx

open System.Net.Http
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

type HttpFuncResult<'r, 'err> =  Task<Result<Context<'r>, HandlerError<'err>>>

type HttpFunc<'a, 'r, 'err> = Context<'a> -> HttpFuncResult<'r, 'err>

type NextFunc<'a, 'r, 'err> = HttpFunc<'a, 'r, 'err>

type HttpHandler<'a, 'b, 'r, 'err> = NextFunc<'b, 'r, 'err> -> Context<'a> -> HttpFuncResult<'r, 'err>

type HttpHandler<'a, 'r, 'err> = HttpHandler<'a, 'a, 'r, 'err>

type HttpHandler<'r, 'err> = HttpHandler<HttpResponseMessage, 'r, 'err>

type HttpHandler<'err> = HttpHandler<HttpResponseMessage, 'err>

[<AutoOpen>]
module Handler =
    let iterate1 (f : unit -> seq<int>) =
        for e in f() do printfn "%d" e
    let iterate2 (f : unit -> #seq<int>) =
        for e in f() do printfn "%d" e

    let finishEarly<'a, 'err> : HttpFunc<'a, 'a, 'err> = Ok >> Task.FromResult

    /// Run the HTTP handler in the given context.
    let runHandler (handler: HttpHandler<'a,'r,'r, 'err>) (ctx : Context<'a>) : Task<Result<'r, HandlerError<'err>>> =
        task {
            let! result = handler finishEarly ctx
            match result with
            | Ok a -> return Ok a.Response
            | Error err -> return Error err
        }
    let map (mapper: 'a -> 'b) (next : NextFunc<'b,'r, 'err>) (ctx : Context<'a>) : HttpFuncResult<'r, 'err> =
        next { Request = ctx.Request; Response = (mapper ctx.Response) }

    let compose (first : HttpHandler<'a, 'b, 'r, 'err>) (second : HttpHandler<'b, 'c, 'r, 'err>) : HttpHandler<'a,'c,'r, 'err> =
        fun (next: NextFunc<'c, 'r, 'err>) (ctx : Context<'a>) ->
            let func =
                next
                |> second
                |> first

            func ctx

    let (>=>) a b =
        compose a b

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let addQuery (query: (string * string) list) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with Query = query } }

    /// Add content to context. These content will be added to the HTTP body of
    /// requests that uses this context.
    let setContent (content: Content) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with Content = Some content } }

    let setResponseType (respType: ResponseType) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with ResponseType = respType }}

    /// Set the method to be used for requests using this context.
    let setMethod<'r, 'err> (method: HttpMethod) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with Method = method; Content = None } }

    let GET<'r, 'err> = setMethod<'r, 'err> HttpMethod.Get
    let POST<'r, 'err> = setMethod<'r, 'err> HttpMethod.Post
    let DELETE<'r, 'err> = setMethod<'r, 'err> HttpMethod.Delete

    /// Run list of HTTP handlers concurrently.
    let concurrent (handlers : HttpHandler<'a, 'b, 'b, 'err> seq) (next: NextFunc<'b list, 'r, 'err>) (ctx: Context<'a>) : HttpFuncResult<'r, 'err> = task {
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
    let sequential (handlers : HttpHandler<'a, 'b, 'b, 'err> seq) (next: NextFunc<'b list, 'r, 'err>) (ctx: Context<'a>) : HttpFuncResult<'r, 'err> = task {
        let res = ResizeArray<Result<Context<'b>, HandlerError<'err>>>()

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

    let extractHeader (header: string) (next: NextFunc<_,_, 'err>) (context: HttpContext) = task {
        let (success, values) = context.Response.Headers.TryGetValues header
        let values = if success then values else Seq.empty

        return! next { Request = context.Request; Response = Ok values }
    }

    /// A catch handler for catching errors and then delegating to the error handler on what to do.
    let catch (errorHandler: HandlerError<'err> -> NextFunc<'a, 'r, 'err>) (next: HttpFunc<'a, 'r, 'err>) (ctx : Context<'a>) = task {
        let! result = next ctx
        match result with
        | Ok ctx -> return Ok ctx
        | Error err -> return! errorHandler err ctx
    }

    /// A error handler for decoding fetch responses. Will ignore successful responses.
    let withError<'a, 'r, 'err> (errorHandler : HttpResponseMessage -> Task<HandlerError<'err>>) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) : HttpFuncResult<'r, 'err> =
        task {
            let response = context.Response
            match response.IsSuccessStatusCode with
            | true -> return! next context
            | false ->
                let! err = errorHandler response
                return err |> Error
        }

