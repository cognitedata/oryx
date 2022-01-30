// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Pipeline

open System

open FSharp.Control.TaskBuilder
open Oryx

module Error =
    /// Handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TContext, 'TSource>
        (errorHandler: 'TContext -> exn -> Pipeline<'TContext, 'TSource>)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TSource> =
        fun success error cancel ->
            let onSuccess' ctx content =
                task {
                    try
                        return! success ctx content
                    with
                    | err when not (err :? PanicException) -> return! (errorHandler ctx err) success error cancel
                }

            let onError' ctx err =
                task {
                    match err with
                    | PanicException err -> return! error ctx err
                    | _ -> do! (errorHandler ctx err) success error cancel
                }

            source onSuccess' onError' cancel

    [<RequireQualifiedAccess>]
    type ChooseState =
        | NoError
        | Error
        | Panic

    /// Choose from a list of pipelines to use. The first middleware that succeeds will be used. Handlers will be
    /// tried until one does not produce any error, or a `PanicException`.
    let choose<'TContext, 'TSource, 'TResult>
        (handlers: (Pipeline<'TContext, 'TSource> -> Pipeline<'TContext, 'TResult>) list)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        fun success error cancel ->
            let exns: ResizeArray<exn> = ResizeArray()

            let success' ctx content =
                task {
                    let mutable state = ChooseState.Error

                    let success'' ctx content =
                        task {
                            exns.Clear() // Clear to avoid buildup of exceptions in streaming scenarios.

                            if state = ChooseState.NoError then
                                return! success ctx content
                        }

                    let error'' _ err =
                        task {
                            match err, state with
                            | PanicException _, st when st <> ChooseState.Panic ->
                                state <- ChooseState.Panic
                                return! error ctx err
                            | SkipException _, st when st = ChooseState.NoError ->
                                // Flag error, but do not record skips.
                                state <- ChooseState.Error
                            | _, ChooseState.Panic ->
                                // Already panic. Ignoring additional error.
                                ()
                            | _, _ ->
                                state <- ChooseState.Error
                                exns.Add(err)
                        }

                    let handlerNext: Pipeline<'TContext, 'TSource> =
                        fun onSuccess _ _ -> task { do! onSuccess ctx content }

                    for handler in handlers do
                        if state = ChooseState.Error then
                            state <- ChooseState.NoError
                            do! handler handlerNext success'' error'' cancel

                    match state, exns with
                    | ChooseState.Panic, _ ->
                        // Panic is sent immediately above
                        ()
                    | ChooseState.Error, exns when exns.Count > 1 -> return! error ctx (AggregateException(exns))
                    | ChooseState.Error, exns when exns.Count = 1 -> return! error ctx exns.[0]
                    | ChooseState.Error, _ -> return! error ctx (SkipException "Choose: No handler matched")
                    | ChooseState.NoError, _ -> ()
                }

            let error' ctx err =
                exns.Clear()
                error ctx err

            let cancel' ctx =
                exns.Clear()
                cancel ctx

            source success' error' cancel'

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TContext, 'TSource, 'TResult>
        (err: Exception)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        fun _ error cancel ->
            fun ctx _ -> error ctx err
            |> Core.swapArgs source error cancel

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TContext, 'TSource, 'TResult>
        (err: Exception)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        fun _ error cancel ->
            fun ctx _ -> error ctx (PanicException(err))
            |> Core.swapArgs source error cancel

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let ofError<'TContext, 'TSource> (_: 'TContext) (err: Exception) : Pipeline<'TContext, 'TSource> =
        fun _ -> raise err

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let ofPanic<'TContext, 'TSource> (_: 'TContext) (error: Exception) : Pipeline<'TContext, 'TSource> =
        fun _ -> raise (PanicException(error))
