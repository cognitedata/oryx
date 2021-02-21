// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

module Chunk =

    let chunk<'TSource, 'TNext, 'TResult>
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'TNext> -> HttpHandler<'TSource, seq<'TResult>>)
        (items: seq<'TNext>)
        : HttpHandler<'TSource, seq<'TResult>> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent)
        |> sequential
        // Collect results
        >=> map (Seq.ofList >> Seq.collect (Seq.collect id))
