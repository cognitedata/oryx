open System.Net.Http

open FSharp.Control.Tasks
open Oryx
open Oryx.ThothJsonNet.ResponseReader
open Thoth.Json.Net

[<EntryPoint>]
let main argv =
    use client = new HttpClient()

    let context = empty |> withHttpClient client

    let request =
        GET
        >> withUrl "https://api.github.com/repos/cognitedata/oryx/releases/latest"
        >> fetch
        >> json (Decode.field "tag_name" Decode.string)

    task {
        let! tag = context |> request |> runAsync

        printfn $"{tag}"

        return 0
    }
    |> (fun t -> t.GetAwaiter().GetResult())
    |> ignore

    0 // return an integer exit code
