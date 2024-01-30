// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System.Threading.Tasks

open FSharp.Control.TaskBuilder
open FsToolkit.ErrorHandling

type IHttpNext<'TSource> =
    abstract member OnSuccessAsync: ctx: HttpContext * content: 'TSource -> Task<unit>
    abstract member OnErrorAsync: ctx: HttpContext * error: exn -> Task<unit>
    abstract member OnCancelAsync: ctx: HttpContext -> Task<unit>

type HttpHandler<'TResult> = IHttpNext<'TResult> -> Task<unit>

module Core =
    /// Swap first with last arg so we can pipe onSuccess
    let swapArgs fn = fun a b c -> fn c a b

    /// A next continuation for observing the final result.
    let finish<'TResult> (tcs: TaskCompletionSource<'TResult>) : IHttpNext<'TResult> =

        { new IHttpNext<'TResult> with
            member x.OnSuccessAsync(_, response) = task { tcs.SetResult response }
            member x.OnErrorAsync(ctx, error) = task { tcs.SetException error }
            member x.OnCancelAsync(ctx) = task { tcs.SetCanceled() } }

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TResult> (handler: HttpHandler<'TResult>) : Task<'TResult> =
        let tcs = TaskCompletionSource<'TResult>()

        task {
            do! finish tcs |> handler
            return! tcs.Task
        }

    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TResult> (handler: HttpHandler<'TResult>) : Task<Result<'TResult, exn>> =
        task {
            try
                let! value = runUnsafeAsync handler
                return Ok value
            with error ->
                return Error error
        }

    /// Produce the given content.
    let singleton<'TSource> (ctx: HttpContext) (content: 'TSource) : HttpHandler<'TSource> =
        fun next -> next.OnSuccessAsync(ctx, content)

    /// Map the content of the middleware.
    let map<'TSource, 'TResult> (mapper: 'TSource -> 'TResult) (source: HttpHandler<'TSource>) : HttpHandler<'TResult> =

        fun next ->
            //fun ctx content -> success ctx (mapper content)
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    try
                        next.OnSuccessAsync(ctx, mapper content)
                    with error ->
                        next.OnErrorAsync(ctx, error)

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Bind the content of the middleware.
    let bind<'TSource, 'TResult>
        (fn: 'TSource -> HttpHandler<'TResult>)
        (source: HttpHandler<'TSource>)
        : HttpHandler<'TResult> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    task {
                        let handler = fn content
                        return! handler next
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    let concurrent<'TSource, 'TResult>
        (merge: HttpContext list -> HttpContext)
        (handlers: seq<HttpHandler<'TResult>>)
        : HttpHandler<'TResult list> =
        fun next ->
            task {
                let res: Result<HttpContext * 'TResult, HttpContext * exn> array =
                    Array.zeroCreate (Seq.length handlers)

                let obv n ctx content = task { res.[n] <- Ok(ctx, content) }

                let obv n =
                    { new IHttpNext<'TResult> with
                        member _.OnSuccessAsync(ctx, content) = task { res.[n] <- Ok(ctx, content) }
                        member _.OnErrorAsync(ctx, err) = task { res.[n] <- Error(ctx, err) }
                        member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }

                let tasks = handlers |> Seq.mapi (fun n handler -> handler (obv n))

                let! _ = Task.WhenAll(tasks)

                let result = res |> List.ofSeq |> List.sequenceResultM

                match result with
                | Ok results ->
                    let results, contents = results |> List.unzip
                    let bs = merge results
                    return! next.OnSuccessAsync(bs, contents)
                | Error(_, err) -> raise err
            }

    /// Run list pipelines sequentially.
    let sequential<'TSource, 'TResult>
        (merge: HttpContext list -> HttpContext)
        (handlers: seq<HttpHandler<'TResult>>)
        : HttpHandler<'TResult list> =
        fun next ->
            task {
                let res = ResizeArray<Result<HttpContext * 'TResult, HttpContext * exn>>()

                let obv =
                    { new IHttpNext<'TResult> with
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
                | Error(_, err) -> raise err
            }

    /// Chunks a sequence of Middlewares into a combination of sequential and concurrent batches.
    let chunk<'TSource, 'TResult>
        (merge: HttpContext list -> HttpContext)
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TSource> -> HttpHandler<seq<'TResult>>)
        (items: seq<'TSource>)
        : HttpHandler<seq<'TResult>> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent merge)
        |> sequential merge
        // Collect results
        |> map (Seq.ofList >> Seq.collect (Seq.collect id))

    /// Handler that skips (ignores) the content and outputs unit.
    let ignoreContent<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<unit> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) = next.OnSuccessAsync(ctx, ())
                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    let cache<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        let mutable cache: (HttpContext * 'TSource) option = None

        fun next ->
            task {
                match cache with
                | Some(ctx, content) -> return! next.OnSuccessAsync(ctx, content)
                | _ ->
                    return!
                        { new IHttpNext<'TSource> with
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
    let empty (ctx: HttpContext) : HttpHandler<unit> =
        fun next -> next.OnSuccessAsync(ctx, ())

    /// Filter content using a predicate function.
    let filter<'TSource> (predicate: 'TSource -> bool) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, value) =
                    task {
                        if predicate value then
                            return! next.OnSuccessAsync(ctx, value)
                    }

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Validate content using a predicate function. Same as filter ut produces an error if validation fails.
    let validate<'TSource> (predicate: 'TSource -> bool) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, value) =
                    if predicate value then
                        next.OnSuccessAsync(ctx, value)
                    else
                        next.OnErrorAsync(ctx, SkipException "Validation failed")

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Retrieves the content.
    let await<'TSource> () (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        source |> map<'TSource, 'TSource> id

    /// Returns the current environment.
    let ask<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<HttpContext> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, _) = next.OnSuccessAsync(ctx, ctx)
                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Update (asks) the context.
    let update<'TSource> (update: HttpContext -> HttpContext) (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    next.OnSuccessAsync(update ctx, content)

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Replaces the value with a constant.
    let replace<'TSource, 'TResult> (value: 'TResult) (source: HttpHandler<'TSource>) : HttpHandler<'TResult> =
        map (fun _ -> value) source
