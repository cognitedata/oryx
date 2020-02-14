// Copyright 2019 Cognite AS

namespace Oryx

open System.IO
open System.Net.Http
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive
open Microsoft.Extensions.Logging

type HttpFuncResult<'r, 'err> =  Task<Result<Context<'r>, HandlerError<'err>>>

type HttpFunc<'a, 'r, 'err> = Context<'a> -> HttpFuncResult<'r, 'err>

type NextFunc<'a, 'r, 'err> = HttpFunc<'a, 'r, 'err>

type HttpHandler<'a, 'b, 'r, 'err> = NextFunc<'b, 'r, 'err> -> Context<'a> -> HttpFuncResult<'r, 'err>

type HttpHandler<'a, 'r, 'err> = HttpHandler<'a, 'a, 'r, 'err>

type HttpHandler<'r, 'err> = HttpHandler<HttpResponseMessage, 'r, 'err>

type HttpHandler<'err> = HttpHandler<HttpResponseMessage, 'err>

[<AutoOpen>]
module Handler =
    /// A next continuation that produces an Ok async result. Used to end the processing pipeline.
    let finishEarly<'a, 'err> : HttpFunc<'a, 'a, 'err> = Ok >> Task.FromResult

    /// Run the HTTP handler in the given context.
    let runAsync (ctx : Context<'a>) (handler: HttpHandler<'a,'r,'r, 'err>) : Task<Result<'r, HandlerError<'err>>> =
        task {
            let! result = handler finishEarly ctx
            match result with
            | Ok a -> return Ok a.Response
            | Error err -> return Error err
        }
    let map (mapper: 'a -> 'b) (next : NextFunc<'b,'r, 'err>) (ctx : Context<'a>) : HttpFuncResult<'r, 'err> =
        next { Request = ctx.Request; Response = (mapper ctx.Response) }

    let inline compose (first : HttpHandler<'a, 'b, 'r, 'err>) (second : HttpHandler<'b, 'c, 'r, 'err>) : HttpHandler<'a,'c,'r, 'err> =
        second >> first

    let (>=>) = compose

    /// Add query parameters to context. These parameters will be added
    /// to the query string of requests that uses this context.
    let addQuery (query: struct (string * string) seq) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with Query = query } }

    /// Add content builder to context. These content will be added to the HTTP body of
    /// requests that uses this context.
    let setContent (builder: unit -> HttpContent) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with ContentBuilder = Some builder } }

    let setResponseType (respType: ResponseType) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with ResponseType = respType }}

    /// Set the method to be used for requests using this context.
    let setMethod<'r, 'err> (method: HttpMethod) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with Method = method } }

    let setUrlBuilder<'r, 'err> (builder: UrlBuilder) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with UrlBuilder = builder } }

    // A basic way to set the request URL. Use custom builders for more advanced usage.
    let setUrl<'r, 'err> (url: string) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        setUrlBuilder (fun _ -> url) next context

    let setLogger (logger: ILogger) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with Logger = Some logger } }

    let setLogLevel (logLevel: LogLevel) (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with LogLevel = logLevel } }

    /// Http GET request. Also clears any content set in the context.
    let GET<'r, 'err> (next: NextFunc<HttpResponseMessage,'r, 'err>) (context: HttpContext) =
        next { context with Request = { context.Request with Method = HttpMethod.Get; ContentBuilder = None } }

    /// Http POST request.
    let POST<'r, 'err> = setMethod<'r, 'err> HttpMethod.Post
    /// Http DELETE request.
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

    let parse<'a, 'r, 'err> (parser : Stream -> 'a) (next: NextFunc<'a, 'r, 'err>) (ctx : Context<HttpResponseMessage>) : Task<Result<Context<'r>,HandlerError<'err>>> =
        task {
            let! stream = ctx.Response.Content.ReadAsStreamAsync ()
            try
                let a = parser stream
                return! next { Request = ctx.Request; Response = a }
            with
            | ex ->
                ctx.Request.Metrics.TraceDecodeErrorInc 1L
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
                ctx.Request.Metrics.TraceDecodeErrorInc 1L
                return Error (Panic ex)
        }

    let extractHeader (header: string) (next: NextFunc<_,_, 'err>) (context: HttpContext) = task {
        let success, values = context.Response.Headers.TryGetValues header
        let values = if success then values else Seq.empty

        return! next { Request = context.Request; Response = Ok values }
    }

    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch (errorHandler: HandlerError<'err> -> NextFunc<'a, 'r, 'err>) (next: HttpFunc<'a, 'r, 'err>) (ctx : Context<'a>) : HttpFuncResult<'r, 'err> = task {
        let! result = next ctx
        match result with
        | Ok ctx -> return Ok ctx
        | Error err -> return! errorHandler err ctx
    }

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError<'a, 'r, 'err> (errorHandler : HttpResponseMessage -> Task<HandlerError<'err>>) (next: NextFunc<HttpResponseMessage,'r, 'err>) (ctx: HttpContext) : HttpFuncResult<'r, 'err> =
        task {
            let response = ctx.Response
            match response.IsSuccessStatusCode with
            | true -> return! next ctx
            | false ->
                ctx.Request.Metrics.TraceFetchErrorInc 1L

                let! err = errorHandler response
                return err |> Error
        }

    /// Logger handler. Needs to be composed in the request after the fetch handler.
    let log (next: HttpFunc<'a, 'r, 'err>) (ctx : Context<'a>) : HttpFuncResult<'r, 'err> =
        let request = ctx.Request

        match request.Logger, request.LogLevel with
        | _, LogLevel.None -> ()
        | Some logger, _ ->
            let uri = request.Extra.TryFind "Url"
            let content = ctx.Request.ContentBuilder |> Option.map (fun builder -> builder ())

            match (uri, content) with
            | Some (Url url), Some content ->
                logger.Log (request.LogLevel, "> {Method} {Url}\n{Content}", request.Method, url, content)
                content.Dispose ()
            | Some (Url url), None ->
                logger.Log (request.LogLevel, "> {Method} {Url}", request.Method, url)
            | _, Some content ->
                logger.Log (request.LogLevel, ">\n{Content}", content)
                content.Dispose ()
            | _, _ ->
                logger.Log (request.LogLevel, ">")

            logger.Log (request.LogLevel, "<\n{content}", ctx.Response)
        | _ -> ()
        next ctx
