module Benchmark.Json

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks.V2

open BenchmarkDotNet.Attributes

open Thoth.Json.Net

open Oryx
open Benchmark.Common
open Classic
open FSharp.Control.Tasks.Builders.Unsafe


[<MemoryDiagnoser>]
[<SimpleJob(targetCount = 20)>]
type JsonBenchmark () =
    let mutable ctx = Context.defaultContext

    [<GlobalSetup>]
    member self.GlobalSetupData () =
        let json =
            (List.replicate 10000 "{ \"name\": \"test\", \"value\": 42}")
            |> String.concat ","
            |> sprintf "[%s]"
            |> fun a -> a.Replace(";", ",")

        let stub =
            Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
            (task {
                let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
                responseMessage.Content <- new StringContent(json)
                return responseMessage
            }))

        let client = new HttpClient(new HttpMessageHandlerStub(stub))

        ctx <-
            Context.defaultContext
            |> Context.setHttpClient client
            |> Context.withUrlBuilder (fun _ -> "http://test.org/")
            |> Context.withHeader ("api-key", "test-key")


    [<Benchmark(Description = "Toth", Baseline = true)>]
    member self.FetchToth () =
        (task {
            let request = req {
                let! a = Common.getList ()
                return a
            }
            let! res = request |> runAsync ctx
            match res with
            | Error e -> failwith <| sprintf "Got error: %A" (e.ToString ())
            | Ok data -> ()
        }).Result

    [<Benchmark(Description = "Utf8Json")>]
    member self.FetchUtf8 () =
        (task {
            let request = req {
                let! a = Common.getJson (readUtf8)
                return a
            }
            let! res = request |> runAsync ctx
            match res with
            | Error e -> failwith <| sprintf "Got error: %A" (e.ToString ())
            | Ok data -> ()
        }).Result

    [<Benchmark(Description = "System.Text.Json")>]
    member self.FetchJson () =
        (task {
            let request = req {
                let! a = Common.getJson (readJson)
                return a
            }
            let! res = request |> runAsync ctx
            match res with
            | Error e -> failwith <| sprintf "Got error: %A" (e.ToString ())
            | Ok data -> ()
        }).Result

    [<Benchmark(Description = "Newtonsoft")>]
    member self.FetchNewtonsoft () =
        (task {
            let request = req {
                let! a = Common.getJson (readNewtonsoft)
                return a
            }
            let! res = request |> runAsync ctx
            match res with
            | Error e -> failwith <| sprintf "Got error: %A" (e.ToString ())
            | Ok data -> ()
        }).Result