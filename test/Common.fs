module Tests.Common

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

open FSharp.Control.TaskBuilder

open Oryx
open Oryx.SystemTextJson.ResponseReader
open Microsoft.Extensions.Logging

type StringableContent(content: string) =
    inherit StringContent(content)
    override this.ToString() = content

type PushStreamContent(content: string) =
    inherit HttpContent()
    let _content = content
    let mutable _disposed = false
    do base.Headers.ContentType <- MediaTypeHeaderValue "application/json"

    override this.ToString() = content

    override this.SerializeToStreamAsync(stream: Stream, _: TransportContext) : Task =
        task {
            let bytes = Encoding.ASCII.GetBytes(_content)
            do! stream.AsyncWrite(bytes)
            do! stream.FlushAsync()
        }
        :> _

    override this.TryComputeLength(length: byref<int64>) : bool =
        length <- -1L
        false

    override this.Dispose(disposing) =
        if _disposed then
            failwith "Already disposed!"
            ()

        base.Dispose(disposing)
        _disposed <- true

type HttpMessageHandlerStub(OnNextAsync: Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) =
    inherit HttpMessageHandler()

    override self.SendAsync
        (
            request: HttpRequestMessage,
            cancellationToken: CancellationToken
        ) : Task<HttpResponseMessage> =
        task { return! OnNextAsync.Invoke(request, cancellationToken) }

let add (a: int) (b: int) = singleton (a + b)

exception TestException of code: int * message: string with
    override this.ToString() = this.message

let error msg source : HttpHandler<'TSource> =
    fail (TestException(code = 400, message = msg)) source

let ofError msg : HttpHandler<'TSource> =
    httpRequest |> fail (TestException(code = 400, message = msg))

let panic msg source : HttpHandler<'TSource> =
    panic (TestException(code = 400, message = msg)) source

/// A bad request handler to use with the `catch` handler. It takes a response to return as Ok.
let badRequestHandler<'TSource> (response: 'TSource) (ctx: HttpContext) (err: exn) : HttpHandler<'TSource> =
    fun next ->
        task {
            match err with
            | TestException(code, _) ->
                match enum<HttpStatusCode> code with
                | HttpStatusCode.BadRequest -> return! next.OnSuccessAsync(ctx, response)
                | _ -> raise (HttpException(ctx, err))
            | _ -> raise (HttpException(ctx, err))
        }

let errorHandler (response: HttpResponse) (_: HttpContent) =
    task { return TestException(code = int response.StatusCode, message = "Got error") }

let options = JsonSerializerOptions()

let get source =
    source
    |> GET
    |> withUrl "http://test.org"
    |> withQuery [ struct ("debug", "true") ]
    |> fetch<'TSource>
    |> withError errorHandler
    |> json options
    |> log

let post content source =
    source
    |> POST
    |> withResponseType ResponseType.JsonValue
    |> withContent content
    |> withCompletion HttpCompletionOption.ResponseHeadersRead
    |> fetch
    |> withError errorHandler
    |> json options
    |> log

//let retryCount = 5
//let retry next ctx = retry shouldRetry 500<ms> retryCount next ctx

type TestLogger<'a>() =
    member val Output: string = String.Empty with get, set
    member val LoggerLevel: LogLevel = LogLevel.Information with get, set

    member this.Log(logLevel: LogLevel, message: string) =
        this.Output <- this.Output + message
        this.LoggerLevel <- logLevel

    interface IDisposable with
        member this.Dispose() = ()

    interface ILogger<'a> with
        member this.Log<'TState>
            (
                logLevel: LogLevel,
                _: EventId,
                state: 'TState,
                exception': exn,
                formatter: Func<'TState, exn, string>
            ) : unit =
            this.Output <- this.Output + formatter.Invoke(state, exception')
            this.LoggerLevel <- logLevel

        member this.IsEnabled(_: LogLevel) : bool = true
        member this.BeginScope<'TState>(_: 'TState) : IDisposable = this :> IDisposable

type TestMetrics() =
    member val Fetches = 0L with get, set
    member val Errors = 0L with get, set
    member val Retries = 0L with get, set
    member val Latency = 0L with get, set
    member val DecodeErrors = 0L with get, set

    interface IMetrics with
        member this.Counter (metric: string) (_: IDictionary<string, string>) (increase: int64) =
            match metric with
            | Metric.FetchInc -> this.Fetches <- this.Fetches + increase
            | Metric.FetchErrorInc -> this.Errors <- this.Errors + increase
            | Metric.FetchRetryInc -> this.Retries <- this.Retries + increase
            | Metric.DecodeErrorInc -> this.DecodeErrors <- this.DecodeErrors + increase
            | _ -> ()

        member this.Gauge (metric: string) (_: IDictionary<string, string>) (update: float) =
            match metric with
            | Metric.FetchLatencyUpdate -> this.Latency <- int64 update
            | _ -> ()
