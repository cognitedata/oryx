namespace Oryx

open System.Net.Http

module Chunk =

    let chunk<'T1, 'T2, 'TResult, 'TError> (chunkSize: int) (maxConcurrency: int) (handler: seq<'T1> -> HttpHandler<HttpResponseMessage, seq<'T2>, seq<'T2>, 'TError>) (items: seq<'T1>) : HttpHandler<HttpResponseMessage, seq<'T2>, 'TResult, 'TError> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map(Seq.map handler >> concurrent)
        |> sequential
        // Collect results
        >=> map (Seq.ofList >> Seq.collect (Seq.collect id))
