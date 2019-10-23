// Copyright 2019 Cognite AS

namespace Oryx

open System
open System.Threading.Tasks

open Microsoft.FSharp.Data.UnitSystems.SI
open FSharp.Control.Tasks.V2.ContextInsensitive


module Retry =
    [<Measure>] type ms

    let private secondsInMilliseconds = 1000<ms/UnitSymbols.s>  // relation between seconds and millisecond

    [<Literal>]
    let DefaultInitialRetryDelay = 150<ms>
    [<Literal>]
    let DefaultMaxBackoffDelay = 120<UnitSymbols.s>

    let rand = System.Random ()

    /// A resonable default should retry handler.
    let shouldRetry (err: ResponseError) =
        let retryCode =
            match err.Code with
            | 429 // Rate limiting
            | 500 // Internal server error
            | 502 // Bad gateway
            | 503 -> true // Service unavailable
            | _ -> false  // Do not retry other responses.

        let retryEx =
            match err.InnerException with
            | Some (:? Net.Http.HttpRequestException) -> true
            | Some (:? Net.WebException) -> true
            | _ -> false // Do not retry other exceptions.

        // Retry if retriable code or retryable exception
        retryCode || retryEx

    /// Retries the given HTTP handler up to `maxRetries` retries with
    /// exponential backoff and up to 2 minute with randomness.
    let rec retry (shouldRetry: ResponseError -> bool) (initialDelay: int<ms>) (maxRetries : int) (handler: HttpHandler<'a,'b,'c>) (next: NextFunc<'b,'c>) (ctx: Context<'a>) : Task<Result<Context<'c>, ResponseError>> = task {
        let exponentialDelay = min (secondsInMilliseconds * DefaultMaxBackoffDelay / 2) (initialDelay * 2)
        let randomDelayScale = min (secondsInMilliseconds * DefaultMaxBackoffDelay / 2) (initialDelay * 2)
        let nextDelay = rand.Next(int randomDelayScale) * 1<ms> + exponentialDelay

        let! result = handler next ctx

        match result with
        | Ok _ -> return result
        | Error err ->
            if shouldRetry err && maxRetries > 0 then
                do! int initialDelay |> Async.Sleep
                return! retry shouldRetry nextDelay (maxRetries - 1) handler next ctx
            else
                return result
    }