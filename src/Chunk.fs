namespace Oryx

open System.Net.Http

module Chunk =

    let chunk<'a, 'b, 'r, 'err> (chunkSize: int) (maxConcurrency: int) (handler: 'a seq -> HttpHandler<HttpResponseMessage, 'b seq, 'b seq, 'err>) (items: 'a seq) : HttpHandler<HttpResponseMessage, 'b seq, 'r, 'err> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map(Seq.map handler >> concurrent)
        |> sequential
        // Collect results
        >=> map (Seq.ofList >> Seq.collect (Seq.collect id))
