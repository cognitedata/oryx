// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System

open FSharp.Control.TaskBuilder
open Oryx

module Error =
    /// Handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TContext, 'TSource, 'TResult>
        (errorHandler: 'TContext -> exn -> HandlerAsync<'TContext, 'TResult>)
        (handlerToCatch: HandlerAsync<'TContext, 'TSource> -> HandlerAsync<'TContext, 'TResult>)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TResult> =
        fun next ->
            fun ctx content ->
                task {
                    let handlerNext: HandlerAsync<'TContext, 'TSource> =
                        fun next -> task { do! next ctx content }

                    try
                        do! next |> handlerToCatch handlerNext
                    with
                    | err when not (err :? PanicException) -> return! (errorHandler ctx err) next
                }
            |> source

    [<RequireQualifiedAccess>]
    type ChooseState =
        | NoError
        | Error
        | Panic

    /// Choose from a list of middlewares to use. The first middleware that succeeds will be used. Handlers will be
    /// tried until one does not produce any error, or a `PanicException`.
    let choose<'TContext, 'TSource, 'TResult>
        (handlers: (HandlerAsync<'TContext, 'TSource> -> HandlerAsync<'TContext, 'TResult>) list)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TResult> =
        fun next ->
            let exns: ResizeArray<exn> = ResizeArray()

            fun ctx content ->
                task {
                    let mutable found = false

                    let handlerNext: HandlerAsync<'TContext, 'TSource> =
                        fun next -> task { do! next ctx content }

                    /// Process handlers until `NoError` or `Panic`. Task is not tail recursive so cannot use a
                    /// recursive function here.
                    for handler in handlers do
                        if not found then
                            try
                                do! next |> handler handlerNext
                                found <- true
                            with
                            | error ->
                                match error with
                                | :? PanicException -> raise error
                                | :? SkipException -> ()
                                | _ -> exns.Add(error)

                    match found, exns.Count with
                    | true, _ -> ()
                    | false, 0 -> raise (SkipException("No choice was given."))
                    | false, 1 -> raise exns.[0]
                    | false, _ -> raise (AggregateException(exns))
                }

            |> source

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TContext, 'TSource, 'TResult>
        (err: Exception)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TResult> =
        fun _ ->
            fun _ _ -> raise err
            |> source

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TContext, 'TSource, 'TResult>
        (error: Exception)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TResult> =
        fun _ ->
            fun _ _ -> raise (PanicException(error))
            |> source

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let ofError<'TContext, 'TSource> (_: 'TContext) (err: Exception) : HandlerAsync<'TContext, 'TSource> =
        fun _ -> raise err

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let ofPanic<'TContext, 'TSource> (_: 'TContext) (error: Exception) : HandlerAsync<'TContext, 'TSource> =
        fun _ -> raise (PanicException(error))
