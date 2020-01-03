module Benchmark.Fetch

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks.V2

open BenchmarkDotNet.Attributes

open Oryx
open Benchmark.Common

[<MemoryDiagnoser>]
type FetchBenchmark () =
    let mutable ctx = Context.defaultContext

    [<GlobalSetup>]
    member self.GlobalSetupData () =
        let json = (List.replicate 1000000 ["""{ "name": "test", "value": 42}"""]).ToString()

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
            |> Context.setUrlBuilder (fun _ -> "http://test.org/")
            |> Context.addHeader ("api-key", "test-key")

    [<Benchmark>]
    member self.Fetch () =

        let req = oryx {
            let! result = get ()
            return result
        }
        task {
            let! res = runAsync req ctx
            match res with
            | Error e -> ()
            | Ok data -> ()
        }