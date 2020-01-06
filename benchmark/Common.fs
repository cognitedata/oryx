module Benchmark.Common

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.V2
open Thoth.Json.Net

open Oryx
open Oryx.ResponseReaders

type TestError = {
    Code : int
    Message : string
}

type HttpMessageHandlerStub (sendAsync: Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) =
    inherit HttpMessageHandler ()
    let sendAsync = sendAsync

    override self.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) : Task<HttpResponseMessage> =
        task {
            return! sendAsync.Invoke(request, cancellationToken)
        }

type TestType = {
    Name: string
    Value: int
}

let decodeError (response: HttpResponseMessage) : Task<HandlerError<TestError>> = task {
    use! stream = response.Content.ReadAsStreamAsync ()
    let decoder = Decode.object (fun get ->
        {
            Code = get.Required.Field "code" Decode.int
            Message = get.Required.Field "message" Decode.string
        })
    let! result = decodeStreamAsync decoder stream
    match result with
    | Ok err -> return ResponseError err
    | Error reason -> return Panic <| JsonDecodeException reason
}

let get () =
    let decoder : Decoder<TestType> =
        Decode.object (fun get -> {
            Name = get.Required.Field "name" Decode.string
            Value = get.Required.Field "value" Decode.int
        })

    GET
    >=> setUrl "http://test"
    >=> fetch
    >=> withError decodeError
    >=> json decoder