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

    /// Retries the given HTTP handler up to `maxRetries` retries with
    /// exponential backoff and up to 2 minute with randomness.
    let rec retry<'a, 'b, 'r, 'err> (shouldRetry: HandlerError<'err> -> bool) (initialDelay: int<ms>) (maxRetries : int) (handler: HttpHandler<'a,'b,'r,'err>) (next: NextFunc<'b,'r, 'err>) (ctx: Context<'a>) : HttpFuncResult<'r, 'err> = task {
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