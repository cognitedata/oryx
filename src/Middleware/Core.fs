// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System.Threading.Tasks

open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open Oryx

/// Represents the next delegate. Note that errors are also pushed down so that error-handling and logging middleware
/// handlers can be placed down-stream. You may also think of it as an observer where the handler may invoke the
/// observer using the OnNextAsync or OnErrorAsync methods.
type IAsyncNext<'TContext, 'TSource> =
    abstract member OnNextAsync : ctx: 'TContext * content: 'TSource -> ValueTask
    abstract member OnErrorAsync : ctx: 'TContext * error: exn -> ValueTask

/// Middleware handler. Use the `Use` method to chain additional middleware handlers after this one.
type IAsyncHandler<'TContext, 'TSource> =
    abstract member Use : next: IAsyncNext<'TContext, 'TSource> -> ValueTask

module Core =
    /// Returns a middleware whose elements are the result of invoking the async transform function on each
    /// element of the source.
    let transform<'TContext, 'TSource, 'TResult>
        (transform: ('TContext * 'TResult -> ValueTask) -> 'TContext -> 'TSource -> ValueTask)
        (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TResult> =
        let useAsync (next: IAsyncNext<'TContext, 'TResult>) =
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnNextAsync(ctx, x) = transform next.OnNextAsync ctx x
                member _.OnErrorAsync(ctx, err) = next.OnErrorAsync(ctx, err) }
            |> source.Use

        { new IAsyncHandler<'TContext, 'TResult> with
            member _.Use o = useAsync o }

    /// A next continuation for observing the final result.
    let finish (tcs: TaskCompletionSource<'TResult>) =
        { new IAsyncNext<'TContext, 'TResult> with
            member _.OnNextAsync(_, response) = unitVtask { tcs.SetResult response }
            member _.OnErrorAsync(_, error) = unitVtask { tcs.SetException error } }

    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TContext, 'TResult> (handler: IAsyncHandler<'TContext, 'TResult>) : Task<Result<'TResult, exn>> =
        let tcs = TaskCompletionSource<'TResult>()

        task {
            do! handler.Use(finish tcs)

            try
                let! value = tcs.Task
                return Ok value
            with
            | err -> return Error err
        }

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TContext, 'TResult> (handler: IAsyncHandler<'TContext, 'TResult>) : Task<'TResult> =
        task {
            let! result = runAsync handler

            match result with
            | Ok value -> return value
            | Error err -> return raise err
        }

    /// Produce the given content.
    let singleton<'TContext, 'TSource> (ctx: 'TContext) (content: 'TSource) : IAsyncHandler<'TContext, 'TSource> =
        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) = unitVtask { do! next.OnNextAsync(ctx, content) } }

    /// Map the content of the middleware.
    let map<'TContext, 'TSource, 'TResult>
        (mapper: 'TSource -> 'TResult)
        (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TResult> =
        { new IAsyncHandler<'TContext, 'TResult> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        try
                            next.OnNextAsync(ctx, mapper content)
                        with
                        | error -> next.OnErrorAsync(ctx, error)

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) }
                |> source.Use }

    /// Bind the content of the middleware.
    let bind<'TContext, 'TSource, 'TResult>
        (fn: 'TSource -> IAsyncHandler<'TContext, 'TResult>)
        (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TResult> =
        { new IAsyncHandler<'TContext, 'TResult> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, value) =
                        unitVtask {
                            let valueObs =
                                { new IAsyncNext<'TContext, 'TResult> with
                                    member _.OnNextAsync(ctx, value) = next.OnNextAsync(ctx, value)

                                    member _.OnErrorAsync(ctx, exn) = unitVtask { return! next.OnErrorAsync(ctx, exn) } }

                            let result =
                                try
                                    fn value |> Ok
                                with
                                | error -> Error error

                            match result with
                            | Ok handler -> return! handler.Use(valueObs)
                            | Error error -> return! next.OnErrorAsync(ctx, error)
                        }

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn) }
                |> source.Use }

    let concurrent<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<IAsyncHandler<'TContext, 'TResult>>)
        : IAsyncHandler<'TContext, 'TResult list> =
        { new IAsyncHandler<'TContext, 'TResult list> with
            member _.Use(next) =
                unitVtask {
                    let res: Result<'TContext * 'TResult, 'TContext * exn> array =
                        Array.zeroCreate (Seq.length handlers)

                    let obv n =
                        { new IAsyncNext<'TContext, 'TResult> with
                            member _.OnNextAsync(ctx, content) = unitVtask { res.[n] <- Ok(ctx, content) }
                            member _.OnErrorAsync(ctx, err) = unitVtask { res.[n] <- Error(ctx, err) } }

                    do!
                        handlers
                        |> Seq.mapi (fun n handler -> handler.Use(obv n).AsTask())
                        |> Task.WhenAll

                    let result = res |> List.ofSeq |> List.sequenceResultM

                    match result with
                    | Ok results ->
                        let results, contents = results |> List.unzip
                        let bs = merge results
                        return! next.OnNextAsync(bs, contents)
                    | Error (ctx, err) -> return! next.OnErrorAsync(ctx, err)
                } }

    /// Run list middlewares sequentially.
    let sequential<'TContext, 'TSource, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (handlers: seq<IAsyncHandler<'TContext, 'TResult>>)

        : IAsyncHandler<'TContext, 'TResult list> =
        { new IAsyncHandler<'TContext, 'TResult list> with
            member _.Use(next) =
                unitVtask {
                    let res = ResizeArray<Result<'TContext * 'TResult, 'TContext * exn>>()

                    let obv =
                        { new IAsyncNext<'TContext, 'TResult> with
                            member _.OnNextAsync(ctx, content) = unitVtask { Ok(ctx, content) |> res.Add }
                            member _.OnErrorAsync(ctx, err) = unitVtask { Error(ctx, err) |> res.Add } }

                    for handler in handlers do
                        do! handler.Use(obv)

                    let result = res |> List.ofSeq |> List.sequenceResultM

                    match result with
                    | Ok results ->
                        let results, contents = results |> List.unzip
                        let bs = merge results
                        return! next.OnNextAsync(bs, contents)
                    | Error (ctx, err) -> return! next.OnErrorAsync(ctx, err)
                } }

    /// Chunks a sequence of Middlewares into a combination of sequential and concurrent batches.
    let chunk<'TContext, 'TSource, 'TNext, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TNext> -> IAsyncHandler<'TContext, seq<'TResult>>)
        (items: seq<'TNext>)
        : IAsyncHandler<'TContext, seq<'TResult>> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent merge)
        |> sequential merge
        // Collect results
        |> map (Seq.ofList >> Seq.collect (Seq.collect id))

    /// Handler that skips (ignores) the content and outputs unit.
    let skip<'TContext, 'TSource> (source: IAsyncHandler<'TContext, 'TSource>) =
        { new IAsyncHandler<'TContext, unit> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnNextAsync(ctx, content = ())
                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error) }
                |> source.Use }


    let cache<'TContext, 'TSource> (source: IAsyncHandler<'TContext, 'TSource>) =
        let mutable cache: ('TContext * 'TSource) option = None

        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) =
                unitVtask {
                    match cache with
                    | Some (ctx, content) -> return! next.OnNextAsync(ctx, content = content)
                    | _ ->
                        return!
                            { new IAsyncNext<'TContext, 'TSource> with
                                member _.OnNextAsync(ctx, content) =
                                    unitVtask {
                                        cache <- Some(ctx, content)
                                        return! next.OnNextAsync(ctx, content = content)
                                    }

                                member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error) }
                            |> source.Use
                } }

    /// Never produces a result.
    let never =
        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) = unitVtask { () } }

    /// Completes the current request.
    let empty<'TContext> (ctx: 'TContext) =
        { new IAsyncHandler<'TContext, unit> with
            member _.Use(next) =
                unitVtask {
                    return! next.OnNextAsync(ctx, ())
                } }

    /// Filter content using a predicate function.
    let filter<'TContext, 'TSource>
        (predicate: 'TSource -> bool)
        (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TSource> =
        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, value) =
                        unitVtask {
                            if predicate value then
                                return! next.OnNextAsync(ctx, content = value)
                        }

                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error) }
                |> source.Use }

    /// Validate content using a predicate function. Same as filter ut produces an error if validation fails.
    let validate<'TContext, 'TSource>
        (predicate: 'TSource -> bool)
        (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TSource> =
        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, value) =
                        if predicate value then
                            next.OnNextAsync(ctx, content = value)
                        else
                            next.OnErrorAsync(ctx, SkipException("Validation failed"))

                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error) }
                |> source.Use }

    /// Retrieves the content.
    let await<'TContext, 'TSource> () = map<'TContext, 'TSource, 'TSource> id

    /// Returns the current environment.
    let ask (source: IAsyncHandler<'TContext, 'TSource>) =
        { new IAsyncHandler<'TContext, 'TContext> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnNextAsync(ctx, ctx)
                    member __.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error) }
                |> source.Use }

    /// Update (asks) the context.
    let update<'TContext, 'TSource> (update: 'TContext -> 'TContext) (source: IAsyncHandler<'TContext, 'TSource>) =
        { new IAsyncHandler<'TContext, 'TSource> with
            override _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        try
                            next.OnNextAsync(update ctx, content)
                        with
                        | error -> next.OnErrorAsync(ctx, error)

                    member __.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error) }
                |> source.Use }

    /// Replaces the value with a constant.
    let replace<'TContext, 'TSource, 'TResult> (value: 'TResult) (source: IAsyncHandler<'TContext, 'TSource>) =
        map (fun _ -> value) source

//[<AutoOpen>]
module Extensions =
    type IAsyncHandler<'TResource, 'TSource> with
        /// Subscribe using plain task returning functions.
        member _.Use(?onNextAsync: 'TSource -> ValueTask, ?onErrorAsync: exn -> ValueTask) =
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnNextAsync(ctx, content) =
                    unitVtask {
                        match onNextAsync with
                        | Some fn -> return! fn content
                        | None -> return ()
                    }

                member _.OnErrorAsync(ctx, exn) =
                    unitVtask {
                        match onErrorAsync with
                        | Some fn -> return! fn exn
                        | None -> return ()
                    } }

        /// Subscribe using a task returning function taking a result. Invokations of `OnNextAsync` will result in `Ok`
        /// while `OnErrorAsync` and `OnCompletedAsync` will produce `Error`. `OnCompletedAsync` will produce
        /// `OperationCanceledException`.
        member _.Use(next: Result<'TSource, exn> -> ValueTask) =
            { new IAsyncNext<'TContext, 'TSource> with
                member _.OnNextAsync(ctx, content) = next (Ok content)
                member _.OnErrorAsync(ctx, exn) = next (Error exn) }
