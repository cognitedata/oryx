// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks

[<AutoOpen>]
module Error =
    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch (errorHandler: exn -> IHttpHandler<'TSource>) : IHttpHandler<'TSource> =
        { new IHttpHandler<'TSource> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, ?content) = next.OnNextAsync(ctx, ?content = content)

                    member _.OnErrorAsync(ctx, err) =
                        task {
                            let handler = (errorHandler err).Subscribe(next)
                            return! handler.OnNextAsync(ctx)
                        }

                    member _.OnCompletedAsync() = next.OnCompletedAsync() } }


    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let throw<'TSource, 'TResult> (error: Exception) : IHttpHandler<'TSource, 'TResult> =
        { new IHttpHandler<'TSource, 'TResult> with
            member _.Subscribe(next) =
                { new IHttpNext<'TSource> with
                    member _.OnNextAsync(ctx, _) = next.OnErrorAsync(ctx, error)
                    member _.OnErrorAsync(ctx, error) = next.OnErrorAsync(ctx, error)
                    member _.OnCompletedAsync() = next.OnCompletedAsync() } }


    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError (errorHandler: HttpResponse -> HttpContent option -> Task<exn>) : IHttpHandler<HttpContent> =
        { new IHttpHandler<HttpContent> with
            member _.Subscribe(next) =
                { new IHttpNext<HttpContent> with
                    member _.OnNextAsync(ctx, ?content) =
                        task {
                            let response = ctx.Response

                            match response.IsSuccessStatusCode with
                            | true -> return! next.OnNextAsync(ctx, ?content = content)
                            | false ->
                                ctx.Request.Metrics.Counter Metric.FetchErrorInc Map.empty 1L

                                let! err = errorHandler response content
                                return! next.OnErrorAsync(ctx, err)
                        }

                    member _.OnErrorAsync(ctx, err) = next.OnErrorAsync(ctx, err)
                    member _.OnCompletedAsync() = next.OnCompletedAsync() } }
