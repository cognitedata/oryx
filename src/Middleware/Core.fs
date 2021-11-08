// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System.Threading.Tasks

open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open Oryx


type NextAsync<'TContext, 'TSource> = 'TContext -> 'TSource -> ValueTask

/// Middleware handler. Use the `Use` method to chain additional middleware handlers after this one.
type HandlerAsync<'TContext, 'TSource> = NextAsync<'TContext, 'TSource> -> ValueTask

module Core =
    /// A next continuation for observing the final result.
    let finish (tcs: TaskCompletionSource<'TResult>) = fun _ response -> unitVtask { tcs.SetResult response }

    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TContext, 'TResult> (handler: HandlerAsync<'TContext, 'TResult>) : Task<Result<'TResult, exn>> =
        let tcs = TaskCompletionSource<'TResult>()

        task {
            do! handler (finish tcs)

            try
                let! value = tcs.Task
                return Ok value
            with
            | err -> return Error err
        }

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TContext, 'TResult> (handler: HandlerAsync<'TContext, 'TResult>) : Task<'TResult> =
        task {
            let! result = runAsync handler

            match result with
            | Ok value -> return value
            | Error err -> return raise err
        }

    /// Produce the given content.
    let singleton<'TContext, 'TSource> (ctx: 'TContext) (content: 'TSource) : HandlerAsync<'TContext, 'TSource> =
        fun next -> unitVtask { do! next ctx content }

    /// Map the content of the middleware.
    let map<'TContext, 'TSource, 'TResult>
        (mapper: 'TSource -> 'TResult)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TResult> =
        fun next ->
            fun ctx content -> next ctx (mapper content)
            |> source

    /// Bind the content of the middleware.
    let bind<'TContext, 'TSource, 'TResult>
        (fn: 'TSource -> HandlerAsync<'TContext, 'TResult>)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TResult> =
        fun next ->
            fun _ value ->
                unitVtask {
                    let handler = fn value
                    return! handler(next)
                }
            |> source

    let concurrent<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<HandlerAsync<'TContext, 'TResult>>)
        : HandlerAsync<'TContext, 'TResult list> =
        fun next ->
            unitVtask {
                let res: Result<'TContext * 'TResult, 'TContext * exn> array =
                    Array.zeroCreate (Seq.length handlers)

                let obv n ctx content = unitVtask { res.[n] <- Ok(ctx, content) }

                do!
                    handlers
                    |> Seq.mapi (fun n handler -> handler(obv n).AsTask())
                    |> Task.WhenAll

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = merge results
                    return! next bs contents
                | Error (_, err) -> raise err
            }

    /// Run list middlewares sequentially.
    let sequential<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<HandlerAsync<'TContext, 'TResult>>)
        : HandlerAsync<'TContext, 'TResult list> =
        fun next ->
            unitVtask {
                let res = ResizeArray<Result<'TContext * 'TResult, 'TContext * exn>>()

                let obv ctx content = unitVtask { Ok(ctx, content) |> res.Add }

                for handler in handlers do
                    do! handler obv

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = merge results
                    return! next bs contents
                | Error (_, err) -> raise err
            }

    /// Chunks a sequence of Middlewares into a combination of sequential and concurrent batches.
    let chunk<'TContext, 'TSource, 'TNext, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TNext> -> HandlerAsync<'TContext, seq<'TResult>>)
        (items: seq<'TNext>)
        : HandlerAsync<'TContext, seq<'TResult>> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent merge)
        |> sequential merge
        // Collect results
        |> map (Seq.ofList >> Seq.collect (Seq.collect id))

    /// Handler that skips (ignores) the content and outputs unit.
    let ignoreContent<'TContext, 'TSource> (source: HandlerAsync<'TContext, 'TSource>) : HandlerAsync<'TContext, unit> =
        fun next ->
            fun ctx _ -> next ctx ()
            |> source

    let cache<'TContext, 'TSource> (source: HandlerAsync<'TContext, 'TSource>) : HandlerAsync<'TContext, 'TSource> =
        let mutable cache: ('TContext * 'TSource) option = None

        fun next ->
            unitVtask {
                match cache with
                | Some (ctx, content) -> return! next ctx content
                | _ ->
                    return!
                        fun ctx content ->
                            unitVtask {
                                cache <- Some(ctx, content)
                                return! next ctx content
                            }
                        |> source
            }

    /// Never produces a result.
    let never _ = unitVtask { () }

    /// Completes the current request.
    let empty<'TContext> (ctx: 'TContext) : HandlerAsync<'TContext, unit> =
        fun next -> unitVtask { return! next ctx () }

    /// Filter content using a predicate function.
    let filter<'TContext, 'TSource>
        (predicate: 'TSource -> bool)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TSource> =
        fun next ->
            fun ctx value ->
                unitVtask {
                    if predicate value then
                        return! next ctx value
                }
            |> source

    /// Validate content using a predicate function. Same as filter ut produces an error if validation fails.
    let validate<'TContext, 'TSource>
        (predicate: 'TSource -> bool)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TSource> =
        fun next ->
            fun ctx value ->
                if predicate value then
                    next ctx value
                else
                    ServiceError.skip "Validation failed"
            |> source

    /// Retrieves the content.
    let await<'TContext, 'TSource> () = map<'TContext, 'TSource, 'TSource> id

    /// Returns the current environment.
    let ask<'TContext, 'TSource> (source: HandlerAsync<'TContext, 'TSource>) : HandlerAsync<'TContext, 'TContext> =
        fun next ->
            fun ctx _ -> next ctx ctx
            |> source

    /// Update (asks) the context.
    let update<'TContext, 'TSource> (update: 'TContext -> 'TContext) (source: HandlerAsync<'TContext, 'TSource>) =
        fun next ->
            fun ctx -> next (update ctx)
            |> source

    /// Replaces the value with a constant.
    let replace<'TContext, 'TSource, 'TResult> (value: 'TResult) (source: HandlerAsync<'TContext, 'TSource>) =
        map (fun _ -> value) source
