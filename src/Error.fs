// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System
open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks.V2.ContextInsensitive

[<AutoOpen>]
module Error =
    /// Catch handler for catching errors and then delegating to the error handler on what to do.
    let catch (errorHandler: exn -> HttpHandler<'TSource>): HttpHandler<'TSource> =
        HttpHandler
        <| fun next ->
            { new IHttpNext<'TSource> with
                member _.NextAsync(ctx, ?content) = next.NextAsync(ctx, ?content = content)

                member _.ErrorAsync(ctx, err) =
                    task {
                        let handler = (errorHandler err).Subscribe(next)
                        return! handler.NextAsync(ctx)
                    } }

    /// Error handler for forcing error. Use with e.g `req` computational expression if you need to "return" an error.
    let throw<'TSource, 'TResult> (error: Exception): HttpHandler<'TSource, 'TResult> =
        HttpHandler
        <| fun next ->
            { new IHttpNext<'TSource> with
                member _.NextAsync(ctx, _) = next.ErrorAsync(ctx, error)
                member _.ErrorAsync(ctx, error) = next.ErrorAsync(ctx, error) }

    /// Error handler for decoding fetch responses into an user defined error type. Will ignore successful responses.
    let withError
        (errorHandler: HttpResponse -> HttpContent option -> Task<exn>)
        : HttpHandler<HttpContent, HttpContent> =
        HttpHandler
        <| fun next ->
            { new IHttpNext<HttpContent> with
                member _.NextAsync(ctx, ?content) =
                    task {
                        let response = ctx.Response

                        match response.IsSuccessStatusCode with
                        | true -> return! next.NextAsync(ctx, ?content = content)
                        | false ->
                            ctx.Request.Metrics.Counter Metric.FetchErrorInc Map.empty 1L

                            let! err = errorHandler response content
                            return! next.ErrorAsync(ctx, err)
                    }

                member _.ErrorAsync(ctx, err) = next.ErrorAsync(ctx, err) }
