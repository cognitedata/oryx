open Oryx
open System.Net.Http
open Oryx.ThothJsonNet.ResponseReader
open Thoth.Json.Net
open FSharp.Control.Tasks

[<EntryPoint>]
let main argv =
    use client = new HttpClient()

    let context =
        Context.defaultContext
        |> Context.withHttpClient client

    let response =
        GET
        >=> withUrl "https://api.github.com/repos/cognitedata/oryx/releases/latest"
        >=> fetch
        >=> json (Decode.field "tag_name" Decode.string)

    let _ =
        (task {
            let! tag = (response |> runAsync context)

            printfn "%A" tag

            return 0
         })
            .GetAwaiter()
            .GetResult()

    0 // return an integer exit code
