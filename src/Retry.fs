// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System

open Microsoft.FSharp.Data.UnitSystems.SI
open FSharp.Control.Tasks.V2.ContextInsensitive


module Retry =
    [<Measure>]
    type ms

    let private secondsInMilliseconds = 1000<ms/UnitSymbols.s> // relation between seconds and millisecond

    [<Literal>]
    let DefaultInitialRetryDelay = 150<ms>

    [<Literal>]
    let DefaultMaxBackoffDelay = 120<UnitSymbols.s>

    let rand = Random()

/// Retries the given HTTP handler up to `maxRetries` retries with exponential backoff and up to 2 minute with
/// randomness.
// let rec retry<'T, 'TResult, 'TError>
//     (shouldRetry: HandlerError<'TError> -> bool)
//     (initialDelay: int<ms>)
//     (maxRetries: int)
//     (next: HttpFunc<'T, 'TResult, 'TError>)
//     (ctx: Context<'T>)
//     : HttpFuncResult<'TResult, 'TError>
//     =
//     task {
//         let exponentialDelay =
//             min (secondsInMilliseconds * DefaultMaxBackoffDelay / 2) (initialDelay * 2)

//         let randomDelayScale =
//             min (secondsInMilliseconds * DefaultMaxBackoffDelay / 2) (initialDelay * 2)

//         let nextDelay =
//             rand.Next(int randomDelayScale) * 1<ms>
//             + exponentialDelay

//         let! result = next ctx

//         match result with
//         | Ok _ -> return result
//         | Error err ->
//             if shouldRetry err && maxRetries > 0 then
//                 do! int initialDelay |> Async.Sleep
//                 ctx.Request.Metrics.Counter Metric.FetchRetryInc Map.empty 1L
//                 return! retry shouldRetry nextDelay (maxRetries - 1) next ctx
//             else
//                 return result
//     }
