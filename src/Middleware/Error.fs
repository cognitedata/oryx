// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System
open System.Threading.Tasks

open FSharp.Control.Tasks
open Oryx

[<AutoOpen>]
module Error =
    /// Handler for protecting the pipeline from exceptions and protocol violations.
    let protect<'TContext, 'TSource> =
        { new IAsyncMiddleware<'TContext, 'TSource> with
            member _.Subscribe(next) =
                let mutable stopped = false

                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            match stopped with
                            | false ->
                                try
                                    return! next.OnNextAsync(ctx, content)
                                with err ->
                                    stopped <- true
                                    return! next.OnErrorAsync(ctx, err)
                            | _ -> ()
                        }

                    member _.OnErrorAsync(ctx, err) =
                        task {
                            match stopped with
                            | false ->
                                stopped <- true
                                return! next.OnErrorAsync(ctx, err)
                            | _ -> ()
                        }

                    member _.OnCompletedAsync(ctx) =
                        task {
                            match stopped with
                            | false ->
                                stopped <- true
                                return! next.OnCompletedAsync(ctx)
                            | _ -> ()
                        } } }

    /// Handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TContext, 'TSource>
        (errorHandler: exn -> IAsyncMiddleware<'TContext, unit, 'TSource>)
        : IAsyncMiddleware<'TContext, 'TSource> =
        { new IAsyncMiddleware<'TContext, 'TSource> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        task {
                            try
                                return! next.OnNextAsync(ctx, content)
                            with err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, err) =
                        task {
                            match err with
                            | PanicException (error) -> return! next.OnErrorAsync(ctx, error)
                            | _ ->
                                let obv = (errorHandler err).Subscribe(next)
                                return! obv.OnNextAsync(ctx, ())
                        }

                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    [<RequireQualifiedAccess>]
    type ChooseState =
        | NoError
        | Error
        | Panic

    /// Choose from a list of middlewares to use. The first middleware that succeeds will be used. Handlers will be
    /// tried until one does not produce any error, or a `PanicException`.
    let choose<'TContext, 'TSource, 'TResult>
        (handlers: IAsyncMiddleware<'TContext, 'TSource, 'TResult> seq)
        : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                let exns : ResizeArray<exn> = ResizeArray()

                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        let mutable state = ChooseState.Error

                        task {
                            let obv =
                                { new IAsyncNext<'TContext, 'TResult> with
                                    member _.OnNextAsync(ctx, content) =
                                        exns.Clear() // Clear to avoid buildup of exceptions in streaming scenarios.
                                        next.OnNextAsync(ctx, content)

                                    member _.OnErrorAsync(ctx, error) =
                                        task {
                                            match error with
                                            | :? SkipException -> state <- ChooseState.Error
                                            | :? PanicException ->
                                                exns.Clear()
                                                exns.Add(error)
                                                state <- ChooseState.Panic
                                            | _ ->
                                                exns.Add(error)
                                                state <- ChooseState.Error
                                        }

                                    member _.OnCompletedAsync _ = Task.FromResult() }

                            /// Proces handlers until `NoError` or `Panic`.
                            for handler in handlers do
                                if state = ChooseState.Error then
                                    state <- ChooseState.NoError
                                    do! handler.Subscribe(obv).OnNextAsync(ctx, content)

                            match state, exns with
                            | ChooseState.Panic, exns -> return! next.OnErrorAsync(ctx, exns.[0])
                            | ChooseState.Error, exns when exns.Count > 1 ->
                                return! next.OnErrorAsync(ctx, AggregateException(exns))
                            | ChooseState.Error, exns when exns.Count = 1 -> return! next.OnErrorAsync(ctx, exns.[0])
                            | ChooseState.Error, _ ->
                                return! next.OnErrorAsync(ctx, SkipException "Choose: No hander matched")
                            | ChooseState.NoError, _ -> ()
                        }

                    member _.OnErrorAsync(ctx, exn) =
                        exns.Clear()
                        next.OnErrorAsync(ctx, exn)

                    member _.OnCompletedAsync(ctx) =
                        exns.Clear()
                        next.OnCompletedAsync(ctx) } }

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TContext, 'TSource, 'TResult> (error: Exception) : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnErrorAsync(ctx, error)
                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TContext, 'TSource, 'TResult> (error: Exception) : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnErrorAsync(ctx, PanicException error)
                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }
