namespace Oryx

open System.Net.Http

module Chunk =

    let chunk<'a, 'b, 'c> (chunkSize: int) (maxConcurrency: int) (handler: 'a seq -> HttpHandler<HttpResponseMessage, 'b seq, 'b seq>) (items: 'a seq) : HttpHandler<HttpResponseMessage, 'b seq, 'c> =
        items
        |> Seq.chunkBySize chunkSize
        |> Seq.chunkBySize maxConcurrency
        |> Seq.map(Seq.map handler >> concurrent)
        |> sequential
        // Collect results
        >=> map (Seq.ofList >> Seq.collect (Seq.collect id))
