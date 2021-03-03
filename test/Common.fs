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

open FSharp.Control.Tasks

open Oryx
open Oryx.SystemTextJson.ResponseReader
open Microsoft.Extensions.Logging

type StringableContent (content: string) =
    inherit StringContent (content)
    override this.ToString() = content

type PushStreamContent (content: string) =
    inherit HttpContent ()
    let _content = content
    let mutable _disposed = false
    do base.Headers.ContentType <- MediaTypeHeaderValue "application/json"

    override this.ToString() = content

    override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
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


type HttpMessageHandlerStub (OnNextAsync: Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) =
    inherit HttpMessageHandler ()

    override self.SendAsync
        (
            request: HttpRequestMessage,
            cancellationToken: CancellationToken
        ) : Task<HttpResponseMessage> =
        task { return! OnNextAsync.Invoke(request, cancellationToken) }

let singleton<'TSource, 'TResult> (value: 'TResult) : IHttpHandler<'TSource, 'TResult> =
    { new IHttpHandler<'TSource, 'TResult> with
        member _.Subscribe(next) =
            { new IHttpNext<'TSource> with
                member _.OnNextAsync(ctx, _) = next.OnNextAsync(ctx, value)
                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }


let add (a: int) (b: int) = singleton (a + b)


exception TestException of code: int * message: string with
    override this.ToString() = this.message

let error msg : IHttpHandler<'TSource, 'TResult> = throw <| TestException(code = 400, message = msg)

/// A bad request handler to use with the `catch` handler. It takes a response to return as Ok.
let badRequestHandler<'TSource> (response: 'TSource) (error: exn) : IHttpHandler<'TSource> =
    { new IHttpHandler<'TSource> with
        member _.Subscribe(next) =
            { new IHttpNext<'TSource> with
                member _.OnNextAsync(ctx, _) =
                    task {
                        match error with
                        | :? TestException as ex ->
                            match enum<HttpStatusCode> (ex.code) with
                            | HttpStatusCode.BadRequest -> return! next.OnNextAsync(ctx, response)

                            | _ -> return! next.OnErrorAsync(ctx, error)
                        | _ -> return! next.OnErrorAsync(ctx, error)
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) } }

let shouldRetry (error: exn) : bool =
    match error with
    | :? TestException -> true
    | _ -> false

let errorHandler (response: HttpResponse) (content: HttpContent option) =
    task { return TestException(code = int response.StatusCode, message = "Got error") }

let options = JsonSerializerOptions()

let get () =
    GET
    >=> withUrl "http://test.org"
    >=> withQuery [ struct ("debug", "true") ]
    >=> fetch
    >=> log
    >=> withError errorHandler
    >=> json options

let post content =
    POST
    >=> withResponseType JsonValue
    >=> withContent content
    >=> withCompletion HttpCompletionOption.ResponseHeadersRead
    >=> fetch
    >=> log
    >=> withError errorHandler
    >=> json options

//let retryCount = 5
//let retry next ctx = retry shouldRetry 500<ms> retryCount next ctx

type TestLogger<'a> () =
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
                eventId: EventId,
                state: 'TState,
                exception': exn,
                formatter: Func<'TState, exn, string>
            ) : unit =
            this.Output <- this.Output + formatter.Invoke(state, exception')
            this.LoggerLevel <- logLevel

        member this.IsEnabled(logLevel: LogLevel) : bool = true
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
