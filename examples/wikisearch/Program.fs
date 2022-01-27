open System.Net.Http
open System.Text.Json

open FSharp.Control.TaskBuilder

open Oryx
open Oryx.ThothJsonNet.ResponseReader
open Thoth.Json.Net

type WikiSearchHit =
    | SearchTerm of string
    | SearchHits of string list

type WikiSearchHits = WikiSearchHits of WikiSearchHit list

let wikiDataItemDecoder: Decoder<WikiSearchHit> =
    Decode.oneOf [ Decode.string |> Decode.map SearchTerm
                   Decode.list Decode.string |> Decode.map SearchHits ]

let wikiDataItemsDecoders: Decoder<WikiSearchHits> =
    Decode.list wikiDataItemDecoder
    |> Decode.map WikiSearchHits

[<Literal>]
let Url = "https://en.wikipedia.org/w/api.php"

let options = JsonSerializerOptions()

let query term =
    [ struct ("action", "opensearch")
      struct ("search", term) ]

let request ctx term =
    ctx
    |> withQuery (query term)
    |> fetch
    |> json wikiDataItemsDecoders

let asyncMain _ =
    task {
        use client = new HttpClient()

        let ctx: HttpHandler<unit> =
            httpRequest
            |> GET
            |> withHttpClient client
            |> withUrl Url
            |> cache

        let! result = request ctx "F#" |> runUnsafeAsync
        printfn $"Result: {result}"

        let! result = request ctx "C#" |> runUnsafeAsync
        printfn $"Result: {result}"
    }

[<EntryPoint>]
let main argv =
    asyncMain().GetAwaiter().GetResult()
    0 // return an integer exit code
