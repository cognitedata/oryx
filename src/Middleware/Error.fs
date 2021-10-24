// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System

open FSharp.Control.Tasks
open Oryx

[<AutoOpen>]
module Error =
    /// Handler for protecting the pipeline from exceptions and protocol violations.
    let protect<'TContext, 'TSource> (source: IAsyncHandler<'TContext, 'TSource>) =
        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) =
                let mutable stopped = false

                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        unitVtask {
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
                        unitVtask {
                            match stopped with
                            | false ->
                                stopped <- true
                                return! next.OnErrorAsync(ctx, err)
                            | _ -> ()
                        }
                } |> source.Use }

    /// Handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TContext, 'TSource>
        (errorHandler: 'TContext -> exn -> IAsyncHandler<'TContext, 'TSource>)
        (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TSource> =
        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        unitVtask {
                            try
                                return! next.OnNextAsync(ctx, content)
                            with err -> return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, err) =
                        unitVtask {
                            match err with
                            | PanicException error -> return! next.OnErrorAsync(ctx, error)
                            | _ ->
                                do! (errorHandler ctx err).Use(next)
                        }

                    } |> source.Use }

    [<RequireQualifiedAccess>]
    type ChooseState =
        | NoError
        | Error
        | Panic

    /// Choose from a list of middlewares to use. The first middleware that succeeds will be used. Handlers will be
    /// tried until one does not produce any error, or a `PanicException`.
    let choose<'TContext, 'TSource, 'TResult>
        (handlers: IAsyncHandler<'TContext, 'TResult> seq)
        (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TResult> =
        { new IAsyncHandler<'TContext, 'TResult> with
            member _.Use(next) =
                let exns : ResizeArray<exn> = ResizeArray()

                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, content) =
                        let mutable state = ChooseState.Error

                        unitVtask {
                            let obv =
                                { new IAsyncNext<'TContext, 'TResult> with
                                    member _.OnNextAsync(ctx, content) =
                                        unitVtask {
                                            exns.Clear() // Clear to avoid buildup of exceptions in streaming scenarios.

                                            if state = ChooseState.NoError then
                                                return! next.OnNextAsync(ctx, content)
                                        }

                                    member _.OnErrorAsync(_, error) =
                                        unitVtask {
                                            match error, state with
                                            | PanicException _, st when st <> ChooseState.Panic ->
                                                state <- ChooseState.Panic
                                                return! next.OnErrorAsync(ctx, error)
                                            | SkipException _, st when st = ChooseState.NoError ->
                                                // Flag error, but do not record skips.
                                                state <- ChooseState.Error
                                            | _, ChooseState.Panic ->
                                                // Already panic. Ignoring additional error.
                                                ()
                                            | _, _ ->
                                                state <- ChooseState.Error
                                                exns.Add(error)
                                        }

                                    }

                            /// Proces handlers until `NoError` or `Panic`.
                            for handler in handlers do
                                if state = ChooseState.Error then
                                    state <- ChooseState.NoError
                                    do! handler.Use(obv)

                            match state, exns with
                            | ChooseState.Panic, _ ->
                                // Panic is sent immediately above
                                ()
                            | ChooseState.Error, exns when exns.Count > 1 ->
                                return! next.OnErrorAsync(ctx, AggregateException(exns))
                            | ChooseState.Error, exns when exns.Count = 1 -> return! next.OnErrorAsync(ctx, exns.[0])
                            | ChooseState.Error, _ ->
                                return! next.OnErrorAsync(ctx, SkipException "Choose: No hander matched")
                            | ChooseState.NoError, _ -> ()
                        }

                    member _.OnErrorAsync(ctx, error) =
                        exns.Clear()
                        next.OnErrorAsync(ctx, error)

                   } |> source.Use }


    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TContext, 'TSource> (error: Exception)
        (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TSource> =
        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnErrorAsync(ctx, error)
                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error)  } |> source.Use }

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TContext, 'TSource> (error: Exception) (source: IAsyncHandler<'TContext, 'TSource>)
        : IAsyncHandler<'TContext, 'TSource> =
        { new IAsyncHandler<'TContext, 'TSource> with
            member _.Use(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnErrorAsync(ctx, PanicException error)
                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error) } |> source.Use }
