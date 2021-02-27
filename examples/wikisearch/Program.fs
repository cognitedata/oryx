open System.Net.Http
open System.Text.Json

open FSharp.Control.Tasks

open Oryx
open Oryx.ThothJsonNet.ResponseReader
open Thoth.Json.Net

type WikiSearchHit =
    | SearchTerm of string
    | SearchHits of string list

type WikiSearchHits = WikiSearchHits of WikiSearchHit list

let wikiDataItemDecoder : Decoder<WikiSearchHit> =
    Decode.oneOf [ Decode.string |> Decode.map SearchTerm; Decode.list Decode.string |> Decode.map SearchHits ]

let wikiDataItemsDecoders : Decoder<WikiSearchHits> =
    Decode.list wikiDataItemDecoder
    |> Decode.map WikiSearchHits

[<Literal>]
let Url = "https://en.wikipedia.org/w/api.php"

let options = JsonSerializerOptions()

let query term = [ struct ("action", "opensearch"); struct ("search", term) ]

let request term =
    GET
    >=> withUrl Url
    >=> withQuery (query term)
    >=> fetch
    >=> json wikiDataItemsDecoders

let asyncMain argv =
    task {
        use client = new HttpClient()

        let ctx =
            Context.defaultContext
            |> Context.withHttpClient client

        let! result = request "F#" |> runAsync ctx
        printfn "Result: %A" result
    }

[<EntryPoint>]
let main argv =
    asyncMain().GetAwaiter().GetResult()
    0 // return an integer exit code
