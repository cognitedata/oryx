// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open System
open System.Threading.Tasks
open FSharp.Control.Tasks

[<AutoOpen>]
module Error =
    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch<'TContext, 'TSource>
        (errorHandler: exn -> IAsyncMiddleware<'TContext, 'TSource>)
        : IAsyncMiddleware<'TContext, 'TSource> =
        { new IAsyncMiddleware<'TContext, 'TSource> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, ?content) = next.OnNextAsync(ctx, ?content = content)

                    member _.OnErrorAsync(ctx, err) =
                        task {
                            let obv = (errorHandler err).Subscribe(next)
                            return! obv.OnNextAsync(ctx)
                        }

                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }

    /// Choose from a list of middlewares to use. The first middleware that succeeds will be used.
    let choose<'TContext, 'TSource, 'TResult>
        (handlers: IAsyncMiddleware<'TContext, 'TSource, 'TResult> seq)
        : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                let exns : ResizeArray<exn> = ResizeArray()

                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, ?content) =
                        let mutable found = false

                        task {
                            let obv =
                                { new IAsyncNext<'TContext, 'TResult> with
                                    member _.OnNextAsync(ctx, ?content) =
                                        found <- true
                                        exns.Clear() // Clear to avoid buildup of exceptions in streaming scenarios.
                                        next.OnNextAsync(ctx, ?content = content)

                                    member _.OnErrorAsync(ctx, error) =
                                        exns.Add error
                                        Task.FromResult()

                                    member _.OnCompletedAsync _ = Task.FromResult() }

                            for handler in handlers do
                                if not found then
                                    do!
                                        handler
                                            .Subscribe(obv)
                                            .OnNextAsync(ctx, ?content = content)

                            match found, exns with
                            | false, exns when exns.Count = 1 -> return! next.OnErrorAsync(ctx, exns.[0])
                            | false, _ -> return! next.OnErrorAsync(ctx, AggregateException(exns))
                            | true, _ -> ()
                        }

                    member _.OnErrorAsync(ctx, exn) =
                        exns.Clear()
                        next.OnErrorAsync(ctx, exn)

                    member _.OnCompletedAsync(ctx) =
                        exns.Clear()
                        next.OnCompletedAsync(ctx) } }

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let throw<'TContext, 'TSource, 'TResult> (error: Exception) : IAsyncMiddleware<'TContext, 'TSource, 'TResult> =
        { new IAsyncMiddleware<'TContext, 'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IAsyncNext<'TContext, 'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnErrorAsync(ctx, error)
                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error)
                    member _.OnCompletedAsync(ctx) = next.OnCompletedAsync(ctx) } }
