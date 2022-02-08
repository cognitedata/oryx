// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Pipeline

open System.Threading.Tasks

open FSharp.Control.TaskBuilder
open FsToolkit.ErrorHandling
open Oryx

type IAsyncNext<'TContext, 'TSource> =
    abstract member OnSuccessAsync: ctx: 'TContext * content: 'TSource -> Task<unit>
    abstract member OnErrorAsync: ctx: 'TContext * error: exn -> Task<unit>
    abstract member OnCancelAsync: ctx: 'TContext -> Task<unit>

type Pipeline<'TContext, 'TSource> = IAsyncNext<'TContext, 'TSource> -> Task<unit>

module Core =
    /// Swap first with last arg so we can pipe onSuccess
    let swapArgs fn = fun a b c -> fn c a b

    /// A next continuation for observing the final result.
    let finish<'TContext, 'TResult> (tcs: TaskCompletionSource<'TResult>) : IAsyncNext<'TContext, 'TResult> =

        { new IAsyncNext<'TContext, 'TResult> with
            member x.OnSuccessAsync(_, response) = task { tcs.SetResult response }
            member x.OnErrorAsync(ctx, error) = task { tcs.SetException error }
            member x.OnCancelAsync(ctx) = task { tcs.SetCanceled() } }

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TContext, 'TResult> (handler: Pipeline<'TContext, 'TResult>) : Task<'TResult> =
        let tcs = TaskCompletionSource<'TResult>()

        task {
            do! finish tcs |> handler
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
        fun next -> next.OnSuccessAsync(ctx, content)

    /// Map the content of the middleware.
    let map<'TContext, 'TSource, 'TResult>
        (mapper: 'TSource -> 'TResult)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =

        fun next ->
            //fun ctx content -> succes ctx (mapper content)
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    try
                        next.OnSuccessAsync(ctx, mapper content)
                    with
                    | error -> next.OnErrorAsync(ctx, error)

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Bind the content of the middleware.
    let bind<'TContext, 'TSource, 'TResult>
        (fn: 'TSource -> Pipeline<'TContext, 'TResult>)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        fun next ->
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    task {
                        let handler = fn content
                        return! handler next
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    let concurrent<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<Pipeline<'TContext, 'TResult>>)
        : Pipeline<'TContext, 'TResult list> =
        fun next ->
            task {
                let res: Result<'TContext * 'TResult, 'TContext * exn> array =
                    Array.zeroCreate (Seq.length handlers)

                let obv n ctx content = task { res.[n] <- Ok(ctx, content) }

                let obv n =
                    { new IAsyncNext<'TContext, 'TResult> with
                        member _.OnSuccessAsync(ctx, content) = task { res.[n] <- Ok(ctx, content) }
                        member _.OnErrorAsync(ctx, err) = task { res.[n] <- Error(ctx, err) }
                        member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }

                let tasks =
                    handlers
                    |> Seq.mapi (fun n handler -> handler (obv n))

                let! _ = Task.WhenAll(tasks)

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = merge results
                    return! next.OnSuccessAsync(bs, contents)
                | Error (_, err) -> raise err
            }

    /// Run list pipelines sequentially.
    let sequential<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<Pipeline<'TContext, 'TResult>>)
        : Pipeline<'TContext, 'TResult list> =
        fun next ->
            task {
                let res = ResizeArray<Result<'TContext * 'TResult, 'TContext * exn>>()

                let obv =
                    { new IAsyncNext<'TContext, 'TResult> with
                        member _.OnSuccessAsync(ctx, content) = task { Ok(ctx, content) |> res.Add }
                        member _.OnErrorAsync(ctx, err) = task { res.Add(Error(ctx, err)) }
                        member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }

                for handler in handlers do
                    do! handler obv

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = merge results
                    return! next.OnSuccessAsync(bs, contents)
                | Error (_, err) -> raise err
            }

    /// Chunks a sequence of Middlewares into a combination of sequential and concurrent batches.
    let chunk<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TSource> -> Pipeline<'TContext, seq<'TResult>>)
        (items: seq<'TSource>)
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
        fun next ->
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnSuccessAsync(ctx, content) = next.OnSuccessAsync(ctx, ())
                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    let cache<'TContext, 'TSource> (source: Pipeline<'TContext, 'TSource>) : Pipeline<'TContext, 'TSource> =
        let mutable cache: ('TContext * 'TSource) option = None

        fun next ->
            task {
                match cache with
                | Some (ctx, content) -> return! next.OnSuccessAsync(ctx, content)
                | _ ->
                    return!
                        { new IAsyncNext<'TContext, 'TSource> with
                            member _.OnSuccessAsync(ctx, content) =
                                task {
                                    cache <- Some(ctx, content)
                                    return! next.OnSuccessAsync(ctx, content)
                                }

                            member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                            member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
                        |> source
            }

    /// Never produces a result.
    let never _ = task { () }

    /// Completes the current request.
    let empty<'TContext> (ctx: 'TContext) : Pipeline<'TContext, unit> =
        fun next -> next.OnSuccessAsync(ctx, ())

    /// Filter content using a predicate function.
    let filter<'TContext, 'TSource>
        (predicate: 'TSource -> bool)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TSource> =
        fun next ->
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnSuccessAsync(ctx, value) =
                    task {
                        if predicate value then
                            return! next.OnSuccessAsync(ctx, value)
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Validate content using a predicate function. Same as filter ut produces an error if validation fails.
    let validate<'TContext, 'TSource>
        (predicate: 'TSource -> bool)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TSource> =
        fun next ->
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnSuccessAsync(ctx, value) =
                    if predicate value then
                        next.OnSuccessAsync(ctx, value)
                    else
                        next.OnErrorAsync(ctx, SkipException "Validation failed")

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Retrieves the content.
    let await<'TContext, 'TSource> () (source: Pipeline<'TContext, 'TSource>) : Pipeline<'TContext, 'TSource> =
        source |> map<'TContext, 'TSource, 'TSource> id

    /// Returns the current environment.
    let ask<'TContext, 'TSource> (source: Pipeline<'TContext, 'TSource>) : Pipeline<'TContext, 'TContext> =
        fun next ->
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnSuccessAsync(ctx, _) = next.OnSuccessAsync(ctx, ctx)
                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Update (asks) the context.
    let update<'TContext, 'TSource>
        (update: 'TContext -> 'TContext)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TSource> =
        fun next ->
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    next.OnSuccessAsync(update ctx, content)

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Replaces the value with a constant.
    let replace<'TContext, 'TSource, 'TResult>
        (value: 'TResult)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        map (fun _ -> value) source
