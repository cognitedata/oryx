// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System

open FSharp.Control.TaskBuilder
open Oryx

module Error =
    /// Handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TContext, 'TSource>
        (errorHandler: 'TContext -> exn -> Pipeline<'TContext, 'TSource>)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TSource> =
        fun onSuccess onError onCancel ->
            let onSuccess' ctx content =
                task {
                    try
                        return! onSuccess ctx content
                    with
                    | err when not (err :? PanicException) -> return! (errorHandler ctx err) onSuccess onError onCancel
                }

            let onError' ctx err =
                task {
                    match err with
                    | PanicException error -> return! onError ctx error
                    | _ -> do! (errorHandler ctx err) onSuccess onError onCancel
                }

            source onSuccess' onError' onCancel

    [<RequireQualifiedAccess>]
    type ChooseState =
        | NoError
        | Error
        | Panic

    /// Choose from a list of middlewares to use. The first middleware that succeeds will be used. Handlers will be
    /// tried until one does not produce any error, or a `PanicException`.
    let choose<'TContext, 'TSource, 'TResult>
        (handlers: (Pipeline<'TContext, 'TSource> -> Pipeline<'TContext, 'TResult>) list)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        fun onSuccess onError onCancel ->
            let exns: ResizeArray<exn> = ResizeArray()

            let onSuccess' ctx content =
                task {
                    let mutable state = ChooseState.Error

                    let onSuccess'' ctx content =
                        task {
                            exns.Clear() // Clear to avoid buildup of exceptions in streaming scenarios.

                            if state = ChooseState.NoError then
                                return! onSuccess ctx content
                        }

                    let onError'' _ error =
                        task {
                            match error, state with
                            | PanicException _, st when st <> ChooseState.Panic ->
                                state <- ChooseState.Panic
                                return! onError ctx error
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

                    let handlerNext: Pipeline<'TContext, 'TSource> =
                        fun onSuccess _ _ -> task { do! onSuccess ctx content }

                    for handler in handlers do
                        if state = ChooseState.Error then
                            state <- ChooseState.NoError
                            do! handler handlerNext onSuccess'' onError'' onCancel

                    match state, exns with
                    | ChooseState.Panic, _ ->
                        // Panic is sent immediately above
                        ()
                    | ChooseState.Error, exns when exns.Count > 1 -> return! onError ctx (AggregateException(exns))
                    | ChooseState.Error, exns when exns.Count = 1 -> return! onError ctx exns.[0]
                    | ChooseState.Error, _ -> return! onError ctx (SkipException "Choose: No handler matched")
                    | ChooseState.NoError, _ -> ()
                }

            let onError' ctx error =
                exns.Clear()
                onError ctx error

            let onCancel' ctx =
                exns.Clear()
                onCancel ctx

            source onSuccess' onError' onCancel'

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TContext, 'TSource, 'TResult>
        (err: Exception)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        fun _ onError onCancel ->
            fun ctx _ -> onError ctx err
            |> Core.swapArgs source onError onCancel

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TContext, 'TSource, 'TResult>
        (error: Exception)
        (source: Pipeline<'TContext, 'TSource>)
        : Pipeline<'TContext, 'TResult> =
        fun _ onError onCancel ->
            fun ctx _ -> onError ctx (PanicException(error))
            |> Core.swapArgs source onError onCancel

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let ofError<'TContext, 'TSource> (_: 'TContext) (err: Exception) : Pipeline<'TContext, 'TSource> =
        fun _ -> raise err

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let ofPanic<'TContext, 'TSource> (_: 'TContext) (error: Exception) : Pipeline<'TContext, 'TSource> =
        fun _ -> raise (PanicException(error))
