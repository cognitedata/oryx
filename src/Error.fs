// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0
namespace Oryx

open System

open FSharp.Control.TaskBuilder
open Oryx

module Error =
    /// Handler for protecting the HttpHandler from exceptions and protocol violations.
    let protect<'TSource> (source: HttpHandler<'TSource>) : HttpHandler<'TSource> =
        fun next ->
            let mutable stopped = false

            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    task {
                        match stopped with
                        | false ->
                            try
                                return! next.OnSuccessAsync(ctx, content)
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

                member _.OnCancelAsync(ctx) =
                    task {
                        match stopped with
                        | false ->
                            stopped <- true
                            return! next.OnCancelAsync(ctx)
                        | _ -> ()
                    } }
            |> source

    /// Handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TSource>
        (errorHandler: HttpContext -> exn -> HttpHandler<'TSource>)
        (source: HttpHandler<'TSource>)
        : HttpHandler<'TSource> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    task {
                        try
                            return! next.OnSuccessAsync(ctx, content)
                        with err ->
                            return! next.OnErrorAsync(ctx, err)
                    }

                member _.OnErrorAsync(ctx, err) =
                    task {
                        match err with
                        | PanicException error -> return! next.OnErrorAsync(ctx, error)
                        | _ -> do! (errorHandler ctx err) next

                    }

                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }

            |> source

    [<RequireQualifiedAccess>]
    type ChooseState =
        | NoError
        | Error
        | Panic

    /// Choose from a list of HttpHandlers to use. The first middleware that succeeds will be used. Handlers will be
    /// tried until one does not produce any error, or a `PanicException`.
    let choose<'TSource, 'TResult>
        (handlers: (HttpHandler<'TSource> -> HttpHandler<'TResult>) list)
        (source: HttpHandler<'TSource>)
        : HttpHandler<'TResult> =
        fun next ->
            let exns: ResizeArray<exn> = ResizeArray()

            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    let mutable state = ChooseState.Error

                    task {
                        let obv =
                            { new IHttpNext<'TResult> with
                                member _.OnSuccessAsync(ctx, content) =
                                    task {
                                        exns.Clear() // Clear to avoid buildup of exceptions in streaming scenarios.

                                        if state = ChooseState.NoError then
                                            return! next.OnSuccessAsync(ctx, content)
                                    }

                                member _.OnErrorAsync(_, error) =
                                    task {
                                        match error, state with
                                        | PanicException(_), st when st <> ChooseState.Panic ->
                                            state <- ChooseState.Panic
                                            return! next.OnErrorAsync(ctx, error)
                                        | SkipException(_), st when st = ChooseState.NoError ->
                                            // Flag error, but do not record skips.
                                            state <- ChooseState.Error
                                        | _, ChooseState.Panic ->
                                            // Already panic. Ignoring additional error.
                                            ()
                                        | _, _ ->
                                            state <- ChooseState.Error
                                            exns.Add(error)
                                    }

                                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }

                        // Process handlers until `NoError` or `Panic`.
                        for handler in handlers do
                            if state = ChooseState.Error then
                                state <- ChooseState.NoError
                                do! handler source obv

                        match state, exns with
                        | ChooseState.Panic, _ ->
                            // Panic is sent immediately above
                            ()
                        | ChooseState.Error, exns when exns.Count > 1 ->
                            return! next.OnErrorAsync(ctx, AggregateException(exns))
                        | ChooseState.Error, exns when exns.Count = 1 -> return! next.OnErrorAsync(ctx, exns.[0])
                        | ChooseState.Error, _ ->
                            return! next.OnErrorAsync(ctx, SkipException "Choose: No handler matched")
                        | ChooseState.NoError, _ -> ()
                    }

                member _.OnErrorAsync(ctx, error) =
                    exns.Clear()
                    next.OnErrorAsync(ctx, error)

                member _.OnCancelAsync(ctx) =
                    exns.Clear()
                    next.OnCancelAsync(ctx) }

            |> source

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let fail<'TSource, 'TResult> (err: Exception) (source: HttpHandler<'TSource>) : HttpHandler<'TResult> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) = next.OnErrorAsync(ctx, err)
                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source

    /// Error handler for forcing a panic error. Use with e.g `req` computational expression if you need break out of
    /// the any error handling e.g `choose` or `catch`•.
    let panic<'TSource, 'TResult> (err: Exception) (source: HttpHandler<'TSource>) : HttpHandler<'TResult> =
        fun next ->
            { new IHttpNext<'TSource> with
                member _.OnSuccessAsync(ctx, content) =
                    next.OnErrorAsync(ctx, PanicException(err))

                member _.OnErrorAsync(ctx, exn) = next.OnErrorAsync(ctx, exn)
                member _.OnCancelAsync(ctx) = next.OnCancelAsync(ctx) }
            |> source
