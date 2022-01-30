// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Pipeline

open System.Threading.Tasks

open FSharp.Control.TaskBuilder
open FsToolkit.ErrorHandling
open Oryx

type OnSuccessAsync<'TContext, 'TSource> = 'TContext -> 'TSource -> Task<unit>
type OnErrorAsync<'TContext> = 'TContext -> exn -> Task<unit>
type OnCancelAsync<'TContext> = 'TContext -> Task<unit>

type Pipeline<'TContext, 'TSource> =
    OnSuccessAsync<'TContext, 'TSource> -> OnErrorAsync<'TContext> -> OnCancelAsync<'TContext> -> Task<unit>


module Core =
    /// Swap first with last arg so we can pipe onSuccess
    let swapArgs fn = fun a b c -> fn c a b

    /// A next continuation for observing the final result.
    let finish<'TContext, 'TResult>
        (tcs: TaskCompletionSource<'TResult>)
        : OnSuccessAsync<'TContext, 'TResult> * OnErrorAsync<'TContext> * OnCancelAsync<'TContext> =
        let onSuccess _ response = task { tcs.SetResult response }
        let onError _ (error: exn) = task { tcs.SetException error }
        let onCancel _ = task { tcs.SetCanceled() }

        (onSuccess, onError, onCancel)

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TContext, 'TResult> (handler: Pipeline<'TContext, 'TResult>) : Task<'TResult> =
        let tcs = TaskCompletionSource<'TResult>()

        task {
            do! finish tcs |||> handler
            return! tcs.Task
        }

    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TContext, 'TResult> (handler: Pipeline<'TContext, 'TResult>) : Task<Result<'TResult, exn>> =
        task {
            try
                let! value = runUnsafeAsync handler
                return Ok value
            with
            | error -> return Error error
        }

    /// Produce the given content.
    let singleton<'TContext, 'TSource> (ctx: 'TContext) (content: 'TSource) : Pipeline<'TContext, 'TSource> =
        fun onSuccess onError onCancel -> task { do! onSuccess ctx content }

    /// Map the content of the middleware.
    let map<'TContext, 'TSource, 'TResult>
        (mapper: 'TSource -> 'TResult)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =

        fun succes ->
            fun ctx content -> succes ctx (mapper content)
            |> source

    /// Bind the content of the middleware.
    let bind<'TContext, 'TSource, 'TResult>
        (fn: 'TSource -> Pipeline<'TContext, 'TResult>)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        fun success error cancel ->
            fun _ value ->
                task {
                    let handler = fn value
                    return! handler success error cancel
                }
            |> swapArgs source error cancel

    let concurrent<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<Pipeline<'TContext, 'TResult>>)
        : Pipeline<'TContext, 'TResult list> =
        fun success error cancel ->
            task {
                let res: Result<'TContext * 'TResult, 'TContext * exn> array =
                    Array.zeroCreate (Seq.length handlers)

                let obv n ctx content = task { res.[n] <- Ok(ctx, content) }

                let tasks =
                    handlers
                    |> Seq.mapi (fun n handler -> handler (obv n) error cancel)

                let! _ = Task.WhenAll(tasks)

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = merge results
                    return! success bs contents
                | Error (_, err) -> raise err
            }

    /// Run list pipelines sequentially.
    let sequential<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<Pipeline<'TContext, 'TResult>>)
        : Pipeline<'TContext, 'TResult list> =
        fun success error cancel ->
            task {
                let res = ResizeArray<Result<'TContext * 'TResult, 'TContext * exn>>()

                let obv ctx content = task { Ok(ctx, content) |> res.Add }

                for handler in handlers do
                    do! handler obv error cancel

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = merge results
                    return! success bs contents
                | Error (_, err) -> raise err
            }

    /// Chunks a sequence of Middlewares into a combination of sequential and concurrent batches.
    let chunk<'TContext, 'TSource, 'TNext, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TNext> -> Pipeline<'TContext, seq<'TResult>>)
        (items: seq<'TNext>)
        : Pipeline<'TContext, seq<'TResult>> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent merge)
        |> sequential merge
        // Collect results
        |> map (Seq.ofList >> Seq.collect (Seq.collect id))

    /// Handler that skips (ignores) the content and outputs unit.
    let ignoreContent<'TContext, 'TSource> (source: Pipeline<'TContext, 'TSource>) : Pipeline<'TContext, unit> =
        fun success ->
            fun ctx _ -> success ctx ()
            |> source

    let cache<'TContext, 'TSource> (source: Pipeline<'TContext, 'TSource>) : Pipeline<'TContext, 'TSource> =
        let mutable cache: ('TContext * 'TSource) option = None

        fun success error cancel ->
            task {
                match cache with
                | Some (ctx, content) -> return! success ctx content
                | _ ->
                    return!
                        fun ctx content ->
                            task {
                                cache <- Some(ctx, content)
                                return! success ctx content
                            }
                        |> swapArgs source error cancel
            }

    /// Never produces a result.
    let never _ = task { () }

    /// Completes the current request.
    let empty<'TContext> (ctx: 'TContext) : Pipeline<'TContext, unit> = fun onSuccess _ _ -> onSuccess ctx ()

    /// Filter content using a predicate function.
    let filter<'TContext, 'TSource>
        (predicate: 'TSource -> bool)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TSource> =
        fun success ->
            fun ctx value ->
                task {
                    if predicate value then
                        return! success ctx value
                }
            |> source

    /// Validate content using a predicate function. Same as filter ut produces an error if validation fails.
    let validate<'TContext, 'TSource>
        (predicate: 'TSource -> bool)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TSource> =
        fun success error cancel ->
            fun ctx value ->
                if predicate value then
                    success ctx value
                else
                    raise (SkipException "Validation failed")
            |> swapArgs source error cancel

    /// Retrieves the content.
    let await<'TContext, 'TSource> () (source: Pipeline<'TContext, 'TSource>) : Pipeline<'TContext, 'TSource> =
        source |> map<'TContext, 'TSource, 'TSource> id

    /// Returns the current environment.
    let ask<'TContext, 'TSource> (source: Pipeline<'TContext, 'TSource>) : Pipeline<'TContext, 'TContext> =
        fun success ->
            fun ctx _ -> success ctx ctx
            |> source

    /// Update (asks) the context.
    let update<'TContext, 'TSource>
        (update: 'TContext -> 'TContext)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TSource> =
        fun success ->
            fun ctx -> success (update ctx)
            |> source

    /// Replaces the value with a constant.
    let replace<'TContext, 'TSource, 'TResult>
        (value: 'TResult)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        map (fun _ -> value) source
