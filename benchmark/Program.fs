module Program

open System
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running

open Benchmark.Fetch
open Benchmark.Json

let add ((a, b): int * int) = a + b

[<MemoryDiagnoser>]
type OryxBenchmark () =

    [<Benchmark>]
    member self.AddTest() = add (54355, 235235) |> ignore

[<EntryPoint>]
let main argv =
    let summary = BenchmarkRunner.Run typeof<JsonBenchmark>
    printfn "%A" summary
    0
