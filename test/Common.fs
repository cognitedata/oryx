module Tests.Common

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net
open System.Net.Http.Headers
open System.Text
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.V2

open Oryx
open Oryx.Retry
open Microsoft.Extensions.Logging

type StringableContent (content: string) =
    inherit StringContent (content)
    override this.ToString () =
        content

type PushStreamContent (content : string) =
    inherit HttpContent ()
    let _content = content
    let mutable _disposed = false
    do
        base.Headers.ContentType <- MediaTypeHeaderValue "application/json"

    override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
        task {
            let bytes = Encoding.ASCII.GetBytes(_content);
            do! stream.AsyncWrite(bytes)
            do! stream.FlushAsync()
        } :> _

    override this.TryComputeLength(length: byref<int64>) : bool =
        length <- -1L
        false

    override this.Dispose (disposing) =
        if _disposed then
            failwith "Already disposed!"
            ()

        base.Dispose(disposing)
        _disposed <- true


type HttpMessageHandlerStub (sendAsync: Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) =
    inherit HttpMessageHandler ()
    let sendAsync = sendAsync

    override self.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) : Task<HttpResponseMessage> =
        task {
            return! sendAsync.Invoke(request, cancellationToken)
        }

let unit (value: 'a) (next: HttpFunc<'a, 'r, 'err>) (context: HttpContext) : HttpFuncResult<'r, 'err> =
    next { Request=context.Request; Response = value }

let add (a: int) (b: int) (next: HttpFunc<int, 'b, 'err>) (context: HttpContext) : HttpFuncResult<'b, 'err>  =
    unit (a + b) next context

exception TestException of string
    with override this.ToString () =
            this.Data0

type TestError = {
    Code : int
    Message : string
}

let apiError msg (next: HttpFunc<'b, 'c, 'err>) (_: Context<'a>) : HttpFuncResult<'c, TestError> =
    { Code = 400; Message = msg } |> ResponseError |> Error |> Task.FromResult

let error msg (next: HttpFunc<'b, 'c, 'err>) (_: Context<'a>) : HttpFuncResult<'c, TestError> =
   TestException msg |> Panic |> Error |> Task.FromResult

/// A bad request handler to use with the `catch` handler. It takes a response to return as Ok.
let badRequestHandler<'a, 'b> (response: 'b) (error: HandlerError<TestError>) (ctx : Context<'a>) = task {
    match error with
    | ResponseError api ->
        match enum<HttpStatusCode>(api.Code) with
        | HttpStatusCode.BadRequest -> return Ok { Request = ctx.Request; Response = response }
        | _ -> return Error error
    | _ ->
        return Error error
}

let shouldRetry (error: HandlerError<TestError>) : bool =
    match error with
    | ResponseError error -> true
    | Panic _ -> false

let errorHandler (response : HttpResponseMessage) = task {
    return { Code = int response.StatusCode; Message = "Got error" } |> ResponseError
}

let json (next: HttpFunc<string, 'c, 'err>) (ctx: Context<HttpResponseMessage>) : HttpFuncResult<'c, 'err> =
    parseAsync (fun _ -> task { return! ctx.Response.Content.ReadAsStringAsync () }) next ctx

let get () =
    GET
    >=> withUrl "http://test.org"
    >=> withQuery [ struct ("debug", "true") ]
    >=> fetch
    >=> log
    >=> withError errorHandler
    >=> json

let post content =
    POST
    >=> withResponseType JsonValue
    >=> withContent content
    >=> fetch
    >=> log
    >=> withError errorHandler
    >=> json

let retryCount = 5
let retry next ctx = retry shouldRetry 0<ms> retryCount next ctx

type TestLogger<'a> () =
    member val Output : string = String.Empty with get, set
    member val LoggerLevel : LogLevel = LogLevel.Information with get, set
    member this.Log(logLevel: LogLevel, message: string) =
        this.Output <- this.Output + message
        this.LoggerLevel <- logLevel

    interface IDisposable with
        member this.Dispose() = ()

    interface ILogger<'a> with
        member this.Log<'TState>(logLevel: LogLevel, eventId: EventId, state: 'TState, exception': exn, formatter: Func<'TState,exn,string>) : unit =
            this.Output <- this.Output + formatter.Invoke(state, exception')
            this.LoggerLevel <- logLevel
        member this.IsEnabled (logLevel: LogLevel): bool = true
        member this.BeginScope<'TState>(state: 'TState) : IDisposable = this :> IDisposable

type TestMetrics () =
    member val Fetches = 0L with get, set
    member val Errors = 0L with get, set
    member val Retries = 0L with get, set
    member val Latency = 0L with get, set
    member val DecodeErrors = 0L with get, set

    interface IMetrics with
        member this.Counter (metric: string) (labels: IDictionary<string, string>) (increase: int64) =
            match metric with
            | Metric.FetchInc -> this.Fetches <- this.Fetches + increase
            | Metric.FetchErrorInc -> this.Errors <- this.Errors + increase
            | Metric.FetchRetryInc -> this.Retries <- this.Retries + increase
            | Metric.DecodeErrorInc -> this.DecodeErrors <- this.DecodeErrors + increase
            | _ -> ()

        member this.Gauge (metric: string) (labels: IDictionary<string, string>) (update: float) =
            match metric with
            | Metric.FetchLatencyUpdate -> this.Latency <- int64 update
            | _ -> ()

