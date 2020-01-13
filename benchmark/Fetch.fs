module Benchmark.Fetch

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
type FetchBenchmark () =
    let mutable ctx = Context.defaultContext

    let compiled =
        (req {
            let! a = Common.get ()
            let! b = Common.get ()

            return b
        }) finishEarly

    [<GlobalSetup>]
    member self.GlobalSetupData () =
        let json = """{ "name": "test", "value": 42}"""

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


    [<Benchmark(Description = "Oryx", Baseline = true)>]
    member self.Fetch () =
        (task {
            let request = req {
                let! a = Common.get ()
                let! b = Common.get ()

                return b
            }
            let! res = runAsync request ctx
            match res with
            | Error e -> failwith <| sprintf "Got error: %A" (e.ToString ())
            | Ok data -> ()
        }).Result

    [<Benchmark(Description = "Oryx Compiled")>]
    member self.FetchCompiled () =
        (task {
            let! res = compiled ctx
            match res with
            | Error e -> failwith <| sprintf "Got error: %A" (e.ToString ())
            | Ok data -> ()
        }).Result

    [<Benchmark(Description = "No next handler")>]
    member self.FetchClassic () =
        (task {
            let request = Classic.Builder.req {
                let! a = ClassicHandler.get ()
                let! b = ClassicHandler.get ()

                return b
            }
            let! res = request ctx
            match res with
            | Error e -> failwith <| sprintf "Got error: %A" (e.ToString ())
            | Ok data -> ()
        }).Result


    [<Benchmark(Description = "No next handler w/Ply")>]
    member self.FetchClassicPly () =
        (uply {
            let req = Ply.Builder.oryx {
                let! a = Ply.Handler.get ()
                let! b = Ply.Handler.get ()

                return b
            }
            let! res = req ctx
            match res with
            | Error e -> failwith <| sprintf "Got error: %A" (e.ToString ())
            | Ok data -> ()
        }).Result
