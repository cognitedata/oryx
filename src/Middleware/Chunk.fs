// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx.Middleware

open Oryx.Middleware.Core

[<AutoOpen>]
module Chunk =

    let chunk<'TContext, 'TSource, 'TNext, 'TResult>
        (merge: 'TContext list -> 'TContext)
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TNext> -> IAsyncMiddleware<'TContext, 'TSource, seq<'TResult>>)
        (items: seq<'TNext>)
        : IAsyncMiddleware<'TContext, 'TSource, seq<'TResult>> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent merge)
        |> sequential merge
        // Collect results
        >=> map (Seq.ofList >> Seq.collect (Seq.collect id))
