// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System
open System.Threading.Tasks

open FSharp.Control.Tasks
open FsToolkit.ErrorHandling

type IAsyncNext<'TContext, 'TSource> =
    abstract member OnNextAsync : ctx: 'TContext * content: 'TSource -> Task<unit>
    abstract member OnErrorAsync : ctx: 'TContext * error: exn -> Task<unit>
    abstract member OnCompletedAsync : ctx: 'TContext -> Task<unit>

type IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
    abstract member Subscribe : next: IAsyncNext<'TContext, 'TResult> -> IAsyncNext<'TContext, 'TSource>

type IAsyncMiddleware<'TContext, 'TSource> = IAsyncMiddleware<'TContext, 'TSource, 'TSource>

module Core =
    /// A next continuation for observing the final result.
    let finish (tcs: TaskCompletionSource<'TResult>) =
        { new IAsyncNext<'TContext, 'TResult> with
            member _.OnNextAsync(_, response) = task { tcs.SetResult response }
            member _.OnErrorAsync(_, error) = task { tcs.SetException error }
            member _.OnCompletedAsync _ = task { tcs.SetCanceled() } }

    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TContext, 'TResult>
        (ctx: 'TContext)
        (handler: IAsyncMiddleware<'TContext, unit, 'TResult>)
        : Task<Result<'TResult, exn>> =
        let tcs = TaskCompletionSource<'TResult>()

        task {
            do! handler.Subscribe(finish tcs).OnNextAsync(ctx, ())

            try
                let! value = tcs.Task
                return Ok value
            with err -> return Error err
        }

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TContext, 'TResult>
        (ctx: 'TContext)
        (handler: IAsyncMiddleware<'TContext, unit, 'TResult>)
        : Task<'TResult> =
        task {
            let! result = runAsync ctx handler

            match result with
            | Ok value -> return value
            | Error err -> return raise err
        }

    /// Produce the given content.
    let singleton<'TContext, 'TSource, 'TResult> (content: 'TResult) : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnNextAsync(ctx, content = content)
                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }


    /// Map the content of the middleware.
    let map<'TContext, 'TSource, 'TResult>
        (mapper: 'TSource -> 'TResult)
        : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) = next.OnNextAsync(ctx, mapper content)
                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Bind the content of the middleware.
    let bind<'TContext, 'TSource, 'TValue, 'TResult>
        (fn: 'TValue -> IAsyncMiddleware<'TContext, 'TSource, 'TResult>)
        (source: IAsyncMiddleware<'TContext, 'TSource, 'TValue>)
        : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        let valueObs =
                            { new IAsyncNext<'TContext, 'TValue> with
                                member _.OnNextAsync(ctx, value) =
                                    task {
                                        let bound : IAsyncMiddleware<'TContext, 'TSource, 'TResult> = fn value
                                        return! bound.Subscribe(next).OnNextAsync(ctx, content)
                                    }

                                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                                member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) }

                        task {
                            let sourceObv = source.Subscribe(valueObs)
                            return! sourceObv.OnNextAsync(ctx, content)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Compose two middlewares into one.
    let inline compose
        (first: IAsyncMiddleware<'TContext, 'T1, 'T2>)
        (second: IAsyncMiddleware<'TContext, 'T2, 'T3>)
        : IAsyncMiddleware<'TContext, 'T1, 'T3> =
        { new IAsyncMiddleware<'TContext, 'T1, 'T3> with
            member _.Subscribe(next) = second.Subscribe(next) |> first.Subscribe }

    /// Composes two middleware into one.
    let (>=>) = compose

    /// Run list of middlewares concurrently.
    let concurrent<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<IAsyncMiddleware<'TContext, 'TSource, 'TResult>>)
        : IAsyncMiddleware<'TContext, 'TSource, 'TResult list> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult list> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            let res : Result<'TContext * 'TResult, exn> array = Array.zeroCreate (Seq.length handlers)

                            let obv n =
                                { new IAsyncNext<'TContext, 'TResult> with
                                    member _.OnNextAsync(ctx, content) = task { res.[n] <- Ok(ctx, content) }
                                    member _.OnErrorAsync(_, err) = task { res.[n] <- Error err }
                                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) }

                            let! _ =
                                handlers
                                |> Seq.mapi (fun n handler -> handler.Subscribe(obv n).OnNextAsync(ctx, content))
                                |> Task.WhenAll

                            let result = res |> List.ofSeq |> List.sequenceResultM

                            match result with
                            | Ok results ->
                                let results, contents = results |> List.unzip
                                let bs = merge results
                                return! next.OnNextAsync(bs, contents)
                            | Error err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Run list middlewares sequentially.
    let sequential<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<IAsyncMiddleware<'TContext, 'TSource, 'TResult>>)
        : IAsyncMiddleware<'TContext, 'TSource, 'TResult list> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult list> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            let res = ResizeArray<Result<'TContext * 'TResult, exn>>()

                            let obv =
                                { new IAsyncNext<'TContext, 'TResult> with
                                    member _.OnNextAsync(ctx, content) = task { Ok(ctx, content) |> res.Add }
                                    member _.OnErrorAsync(_, err) = task { Error err |> res.Add }
                                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) }

                            for handler in handlers do
                                do!
                                    handler.Subscribe(obv)
                                    |> (fun obv -> obv.OnNextAsync(ctx, content))

                            let result = res |> List.ofSeq |> List.sequenceResultM

                            match result with
                            | Ok results ->
                                let results, contents = results |> List.unzip
                                let bs = merge results
                                return! next.OnNextAsync(bs, contents)
                            | Error err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Chunks a sequence of Middlewares into a combination of sequential and concurrent batches.
    let chunk<'TContext, 'TSource, 'TNext, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TNext> -> IAsyncMiddleware<'TContext, 'TSource, seq<'TResult>>)
        (items: seq<'TNext>)
        : IAsyncMiddleware<'TContext, 'TSource, seq<'TResult>> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent merge)
        |> sequential merge
        // Collect results
        >=> map (Seq.ofList >> Seq.collect (Seq.collect id))

    /// Handler that ignores the content and outputs unit.
    let ignore<'TContext, 'TSource> =
        { new IAsyncMiddleware<'TContext, 'TSource, unit> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnNextAsync(ctx, content = ())
                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Validate content using a predicate function.
    let validate<'TContext, 'TSource> (predicate: 'TSource -> bool) : IAsyncMiddleware<'TContext, 'TSource, 'TSource> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TSource> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, value) =
                        if predicate (value) then
                            next.OnNextAsync(ctx, content = value)
                        else
                            next.OnErrorAsync(ctx, Exception("Validation failed"))

                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

[<AutoOpen>]
module Extensions =
    type IAsyncMiddleware<'TResource, 'TSource, 'TResult> with
        /// Subscribe using plain task returning functions.
        member _.Subscribe
            (
                ?onNextAsync: 'TSource -> Task<unit>,
                ?onErrorAsync: exn -> Task<unit>,
                ?onCompletedAsync: unit -> Task<unit>
            ) =
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnNextAsync(ctx, content) =
                    match onNextAsync with
                    | Some fn -> fn (content)
                    | None -> Task.FromResult()

                member _.OnErrorAsync(ctx, exn) =
                    match onErrorAsync with
                    | Some fn -> fn exn
                    | None -> Task.FromResult()

                member _.OnCompletedAsync(ctx) =
                    match onCompletedAsync with
                    | Some fn -> fn ()
                    | None -> Task.FromResult() }

        /// Subscribe using a task returning function taking a result. Invokations of `OnNextAsync` will result in `Ok`
        /// while `OnErrorAsync` and `OnCompletedAsync` will produce `Error`. `OnCompletedAsync` will produce
        /// `OperationCanceledException`.
        member _.Subscribe(next: Result<'TSource, exn> -> Task<unit>) =
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnNextAsync(ctx, content) = next (Ok content)
                member _.OnErrorAsync(ctx, exn) = next (Error exn)
                member _.OnCompletedAsync(ctx) = next (OperationCanceledException() :> exn |> Error) }
