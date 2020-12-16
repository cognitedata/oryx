module Benchmark.Common

open System
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.V2
open Newtonsoft.Json
open Thoth.Json.Net
open Utf8Json

open Oryx
open Oryx.ThothJsonNet
open Oryx.ThothJsonNet.ResponseReader

type TestError = { Code: int; Message: string }

type HttpMessageHandlerStub (sendAsync: Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) =
    inherit HttpMessageHandler()
    let sendAsync = sendAsync

    override self.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken)
                            : Task<HttpResponseMessage> =
        task { return! sendAsync.Invoke(request, cancellationToken) }

[<CLIMutable>]
type TestType = { Name: string; Value: int }

let decodeError (response: HttpResponseMessage): Task<HandlerError<TestError>> =
    task {
        let! stream = response.Content.ReadAsStreamAsync()

        let decoder =
            Decode.object
                (fun get ->
                    {
                        Code = get.Required.Field "code" Decode.int
                        Message = get.Required.Field "message" Decode.string
                    })

        let! result = decodeStreamAsync decoder stream

        match result with
        | Ok err -> return ResponseError err
        | Error reason -> return Panic <| JsonDecodeException reason
    }

let decoder: Decoder<TestType> =
    Decode.object
        (fun get ->
            {
                Name = get.Required.Field "name" Decode.string
                Value = get.Required.Field "value" Decode.int
            })

let listDecoder = Decode.list decoder

let noop (next: HttpFunc<int, int, 'TError>) (ctx: Context<'T>): HttpFuncResult<int, 'TError> =
    task {
        let ctx' = { Request = ctx.Request; Response = 42 }
        return! next ctx'
    }

let get () =
    GET
    >=> withUrl "http://test"
    >=> fetch
    >=> withError decodeError
    >=> json decoder
    >=> noop
    >=> noop

let getList () =

    GET
    >=> withUrl "http://test"
    >=> fetch
    >=> withError decodeError
    >=> json listDecoder

let readUtf8<'T> stream =
    task {
        try
            let! a = (JsonSerializer.DeserializeAsync<'T> stream)
            return Ok a
        with e -> return Error(e.ToString())
    }

let readJson<'T> stream =
    let options = Json.JsonSerializerOptions(AllowTrailingCommas = true)

    task {
        try
            let! a =
                (Json.JsonSerializer.DeserializeAsync<'T>(stream, options))
                    .AsTask()

            return Ok a
        with e -> return Error(e.ToString())
    }

let readNewtonsoft<'T> (stream: IO.Stream) =
    let serializer = JsonSerializer()
    use tr = new StreamReader(stream) // StreamReader will dispose the stream

    use jtr =
        new JsonTextReader(tr, DateParseHandling = DateParseHandling.None)

    task {
        try
            let a = (serializer.Deserialize<'T> jtr)
            return Ok a
        with e -> return Error(e.ToString())
    }

let jsonReader<'T, 'Result, 'TError>
    (reader: Stream -> Task<Result<'T, string>>)
    (next: HttpFunc<'T, 'Result, 'TError>)
    (context: Context<HttpResponseMessage>)
    : HttpFuncResult<'Result, 'TError>
    =
    task {
        use! stream = context.Response.Content.ReadAsStreamAsync()
        let! ret = reader stream

        match ret with
        | Ok result ->
            return!
                next
                    {
                        Request = context.Request
                        Response = result
                    }
        | Error error -> return Error(Panic <| JsonDecodeException error)
    }

let getJson (reader: Stream -> Task<Result<TestType seq, string>>): HttpHandler<unit, TestType seq, 'TNext, TestError> =
    GET
    >=> withUrl "http://test"
    >=> fetch
    >=> withError decodeError
    >=> jsonReader reader
