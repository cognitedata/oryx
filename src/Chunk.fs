namespace Oryx

module Chunk =

    let chunk<'a, 'b, 'c> (chunkSize: int) (maxConcurrency: int) (handler: 'a seq -> HttpHandler<'a, 'b, 'b>) (items: 'a seq) : HttpHandler<'a, 'b list, 'c> =
        let chunks = items |> Seq.chunkBySize chunkSize
        let batches = chunks |> Seq.chunkBySize maxConcurrency

        batches
        |> Seq.map(Seq.map handler >> concurrent)
        |> sequential
        // Collect results
        >=> Handler.map (List.collect id)