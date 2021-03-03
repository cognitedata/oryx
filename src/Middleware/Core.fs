// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System
open System.IO
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks
open FsToolkit.ErrorHandling

type IAsyncNext<'TContext, 'TSource> =
    abstract member OnNextAsync : ctx: 'TContext * ?content: 'TSource -> Task<unit>
    abstract member OnErrorAsync : ctx: 'TContext * error: exn -> Task<unit>
    abstract member OnCompletedAsync : ctx: 'TContext -> Task<unit>

type IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
    abstract member Subscribe : next: IAsyncNext<'TContext, 'TResult> -> IAsyncNext<'TContext, 'TSource>

type IAsyncMiddleware<'TContext, 'TSource> = IAsyncMiddleware<'TContext, 'TSource, 'TSource>

module Core =
    let result (tcs: TaskCompletionSource<'TResult option>) =
        { new IAsyncNext<'TContext, 'TResult> with
            member _.OnNextAsync(_, ?response) = task { tcs.SetResult response }
            member _.OnErrorAsync(_, error) = task { tcs.SetException error }
            member _.OnCompletedAsync _ = task { tcs.SetCanceled() } }

    /// Run the HTTP handler in the given context. Returns content as result type.
    let runAsync<'TContext, 'TSource, 'TResult>
        (ctx: 'TContext)
        (handler: IAsyncMiddleware<'TContext, 'TSource, 'TResult>)
        : Task<Result<'TResult, exn>> =
        let tcs = TaskCompletionSource<'TResult option>()

        task {
            do! handler.Subscribe(result tcs).OnNextAsync(ctx)

            try
                match! tcs.Task with
                | Some value -> return Ok value
                | _ -> return OperationCanceledException() :> Exception |> Error
            with err -> return Error err
        }

    /// Run the HTTP handler in the given context. Returns content and throws exception if any error occured.
    let runUnsafeAsync<'TContext, 'TSource, 'TResult>
        (ctx: 'TContext)
        (handler: IAsyncMiddleware<'TContext, 'TSource, 'TResult>)
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
                    member _.OnNextAsync(ctx, ?content) =
                        match content with
                        | Some content -> next.OnNextAsync(ctx, mapper content)
                        | None -> next.OnNextAsync(ctx)

                    member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    let bind<'TContext, 'TSource, 'TNext, 'TResult>
        (fn: 'TSource -> IAsyncMiddleware<'TContext, 'TNext, 'TResult>)
        : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =

        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        task {
                            match content with
                            | Some content ->
                                let bound : IAsyncMiddleware<'TContext, 'TNext, 'TResult> = fn content
                                return! bound.Subscribe(next).OnNextAsync(ctx)
                            | None -> return! next.OnNextAsync(ctx)
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
                    member _.OnNextAsync(ctx, _) =
                        task {
                            let res : Result<'TContext * 'TResult, exn> array = Array.zeroCreate (Seq.length handlers)

                            let obv n =
                                { new IAsyncNext<'TContext, 'TResult> with
                                    member _.OnNextAsync(ctx, content) =
                                        task {
                                            match content with
                                            | Some content -> res.[n] <- Ok(ctx, content)
                                            | None -> res.[n] <- Error(ArgumentNullException() :> _)
                                        }

                                    member _.OnErrorAsync(_, err) = task { res.[n] <- Error err }
                                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) }

                            let! _ =
                                handlers
                                |> Seq.mapi (fun n handler -> handler.Subscribe(obv n).OnNextAsync ctx)
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
                    member _.OnNextAsync(ctx, _) =
                        task {
                            let res = ResizeArray<Result<'TContext * 'TResult, exn>>()

                            let obv =
                                { new IAsyncNext<'TContext, 'TResult> with
                                    member _.OnNextAsync(ctx, content) =
                                        task {
                                            match content with
                                            | Some content -> Ok(ctx, content) |> res.Add
                                            | None -> Error(ArgumentNullException() :> exn) |> res.Add
                                        }

                                    member _.OnErrorAsync(_, err) = task { Error err |> res.Add }
                                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) }

                            for handler in handlers do
                                do!
                                    handler.Subscribe(obv)
                                    |> (fun obv -> obv.OnNextAsync ctx)

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
