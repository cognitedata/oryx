// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System

open FSharp.Control.Tasks
open Oryx

module Error =
    /// Handler for protecting the pipeline from exceptions and protocol violations.
    let protect<'TContext, 'TSource> (source: HandlerAsync<'TContext, 'TSource>) : HandlerAsync<'TContext, 'TSource> =
        fun next ->
            let mutable stopped = false

            fun ctx content ->
                unitVtask {
                    match stopped with
                    | false ->
                        try
                            return! next ctx content
                        with
                        | err ->
                            stopped <- true
                            ServiceError.error(err)
                    | _ -> ()
                }
            |> source

    /// Handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TContext, 'TSource>
        (errorHandler: 'TContext -> exn -> HandlerAsync<'TContext, 'TSource>)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TSource> =
        fun next ->
            fun ctx content ->
                unitVtask {
                    try
                        return! next ctx content
                    with
                    | err -> return! (errorHandler ctx err) next
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
                unitVtask {
                    let next' ctx content = unitVtask {
                        do! next ctx content
                    }

                    /// Proces handlers until `NoError` or `Panic`.
                    let rec chooser (handlers: (HandlerAsync<'TContext, 'TSource> -> HandlerAsync<'TContext, 'TResult>) list) = unitVtask {
                        match handlers with
                        | handler :: xs ->
                            try
                                do! next' |> handler source
                            with
                            | error ->
                                match error with
                                | ServiceException (ServiceError.Panic _) -> () //reraise ()
                                | ServiceException (ServiceError.Skip _) -> ()
                                | _ -> exns.Add(error)
                                do! chooser xs
                        | [] -> ()
                    }

                    do! chooser handlers

                    match exns.Count with
                    | 0 -> ()
                    | 1 -> raise exns.[0]
                    | _ ->
                        raise (AggregateException(exns))
                }

           |> source

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TContext, 'TSource, 'TResult>
        (err: Exception)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TResult> =
        fun next ->
            fun ctx _ -> ServiceError.error(err)
            |> source

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TContext, 'TSource, 'TResult>
        (error: Exception)
        (source: HandlerAsync<'TContext, 'TSource>)
        : HandlerAsync<'TContext, 'TResult> =
        fun next ->
            fun ctx _ -> ServiceError.panic(error)
            |> source

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let ofError<'TContext, 'TSource> (ctx: 'TContext) (err: Exception) : HandlerAsync<'TContext, 'TSource> =
        fun next -> ServiceError.error err

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let ofPanic<'TContext, 'TSource> (ctx: 'TContext) (error: Exception) : HandlerAsync<'TContext, 'TSource> =
        fun next ->
            ServiceError.panic error
