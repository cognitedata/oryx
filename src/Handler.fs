// Copyright 2019 Cognite AS

namespace Oryx

open System.IO
open System.Net.Http
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

type HandlerError<'TError> =
    /// Request failed with some exception, e.g HttpClient throws an exception, or JSON decode error.
    | Panic of exn
    /// User defined error response.
    | ResponseError of 'TError

type HttpFuncResult<'TResult, 'TError> =  Task<Result<Context<'TResult>, HandlerError<'TError>>>

type HttpFunc<'T, 'TResult, 'TError> = Context<'T> -> HttpFuncResult<'TResult, 'TError>

type NextFunc<'T, 'TResult, 'TError> = HttpFunc<'T, 'TResult, 'TError>

type HttpHandler<'T, 'T2, 'TResult, 'TError> = NextFunc<'T2, 'TResult, 'TError> -> Context<'T> -> HttpFuncResult<'TResult, 'TError>

type HttpHandler<'T, 'TResult, 'TError> = HttpHandler<'T, 'T, 'TResult, 'TError>

type HttpHandler<'T, 'TError> = HttpHandler<HttpResponseMessage, 'T, 'TError>

type HttpHandler<'TError> = HttpHandler<HttpResponseMessage, 'TError>

[<AutoOpen>]
module Handler =
    /// A next continuation that produces an Ok async result. Used to end the processing pipeline.
    let finishEarly<'T, 'TError> : HttpFunc<'T, 'T, 'TError> = Ok >> Task.FromResult

    /// Run the HTTP handler in the given context.
    let runAsync (ctx : Context<'T>) (handler: HttpHandler<'T,'TResult,'TResult, 'TError>) : Task<Result<'TResult, HandlerError<'TError>>> =
        task {
            let! result = handler finishEarly ctx
            match result with
            | Ok a -> return Ok a.Response
            | Error err -> return Error err
        }
    let map (mapper: 'T1 -> 'T2) (next : NextFunc<'T2,'TResult, 'TError>) (ctx : Context<'T1>) : HttpFuncResult<'TResult, 'TError> =
        next { Request = ctx.Request; Response = (mapper ctx.Response) }

    let inline compose (first : HttpHandler<'T1, 'T2, 'TResult, 'TError>) (second : HttpHandler<'T2, 'T3, 'TResult, 'TError>) : HttpHandler<'T1,'T3,'TResult, 'TError> =
        second >> first

    let (>=>) = compose

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let withQuery (query: seq<struct (string * string)>) (next: NextFunc<HttpResponseMessage,'TResult, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with Query = query } }

    /// Add content builder to context. These content will be added to the HTTP body of
    /// requests that uses this context.
    let withContent<'T, 'TError> (builder: unit -> HttpContent) (next: NextFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with ContentBuilder = Some builder } }

    let withResponseType<'T, 'TError> (respType: ResponseType) (next: NextFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with ResponseType = respType }}

    /// Set the method to be used for requests using this context.
    let withMethod<'T, 'TError> (method: HttpMethod) (next: NextFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with Method = method } }

    let withUrlBuilder<'T, 'TError> (builder: UrlBuilder) (next: NextFunc<HttpResponseMessage,'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with UrlBuilder = builder } }

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let withUrl<'T, 'TError> (url: string) (next: NextFunc<HttpResponseMessage, 'T, 'TError>) (context: HttpContext) =
        withUrlBuilder (fun _ -> url) next context

    /// Http GET request. Also clears any content set in the context.
    let GET<'T, 'TError> (next: NextFunc<HttpResponseMessage, 'T, 'TError>) (context: HttpContext) =
        next { context with Request = { context.Request with Method = HttpMethod.Get; ContentBuilder = None } }

    /// Http POST request.
    let POST<'r, 'err> = withMethod<'r, 'err> HttpMethod.Post
    /// Http PUT request.
    let PUT<'r, 'err> = withMethod<'r, 'err> HttpMethod.Put
    /// Http DELETE request.
    let DELETE<'r, 'err> = withMethod<'r, 'err> HttpMethod.Delete
    /// Http Options request.
    let OPTIONS<'r, 'err> = withMethod<'r, 'err> HttpMethod.Options

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

    let parse<'a, 'r, 'err> (parser : Stream -> 'a) (next: NextFunc<'a, 'r, 'err>) (ctx : Context<HttpResponseMessage>) : Task<Result<Context<'r>,HandlerError<'err>>> =
        task {
            let! stream = ctx.Response.Content.ReadAsStreamAsync ()
            try
                let a = parser stream
                return! next { Request = ctx.Request; Response = a }
            with
            | ex ->
                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                return Error (Panic ex)
        }

    let parseAsync<'a, 'r, 'err> (parser : Stream -> Task<'a>) (next: NextFunc<'a, 'r, 'err>) (ctx : Context<HttpResponseMessage>) : Task<Result<Context<'r>,HandlerError<'err>>> =
        task {
            let! stream = ctx.Response.Content.ReadAsStreamAsync ()
            try
                let! a = parser stream
                return! next { Request = ctx.Request; Response = a }
            with
            | ex ->
                ctx.Request.Metrics.Counter Metric.DecodeErrorInc Map.empty 1L
                return Error (Panic ex)
        }

    let extractHeader (header: string) (next: NextFunc<_,_, 'err>) (context: HttpContext) = task {
        let success, values = context.Response.Headers.TryGetValues header
        let values = if success then values else Seq.empty

        return! next { Request = context.Request; Response = Ok values }
    }

