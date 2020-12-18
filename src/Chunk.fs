// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System.Net.Http

module Chunk =

    let chunk<'T1, 'TNext, 'TResult, 'TError>
        (chunkSize: int)
        (maxConcurrency: int)
        (handler: seq<'T1> -> HttpHandler<unit, seq<'TNext>, seq<'TNext>, 'TError>)
        (items: seq<'T1>)
        : HttpHandler<unit, seq<'TNext>, 'TResult, 'TError>
        =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map (Seq.map handler >> concurrent)
        |> sequential
        // Collect results
        >=> map (Seq.ofList >> Seq.collect (Seq.collect id))
