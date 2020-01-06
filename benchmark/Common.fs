module Benchmark.Common

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.Text
open System.Text.Json.Serialization
open FSharp.Control.Tasks.V2
open Thoth.Json.Net
open Utf8Json

open Oryx
open Oryx.ResponseReaders
open System.IO
open Newtonsoft.Json

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

type TestType2 (name: string, value: int) =
    member this.Name = name
    member this.Value = value
    new () = TestType2("", 0)

let decodeError (response: HttpResponseMessage) : Task<HandlerError<TestError>> = task {
    let! stream = response.Content.ReadAsStreamAsync ()
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

let decoder : Decoder<TestType> =
    Decode.object (fun get -> {
        Name = get.Required.Field "name" Decode.string
        Value = get.Required.Field "value" Decode.int
    })
let listDecoder = Decode.list decoder

let noop (next: NextFunc<int, int, 'err>) (ctx: Context<'a>) : HttpFuncResult<int, 'err> =
    task {
        let ctx' = { Request = ctx.Request; Response = 42 }
        return! next ctx'
    }

let get () =
    GET
    >=> setUrl "http://test"
    >=> fetch
    >=> withError decodeError
    >=> json decoder
    >=> noop
    >=> noop

let getList () =

    GET
    >=> setUrl "http://test"
    >=> fetch
    >=> withError decodeError
    >=> json listDecoder

let readUtf8<'a> stream =
    try
        task {
            let! a = (JsonSerializer.DeserializeAsync<'a> stream)
            return Ok a
        }
    with
    e -> task { return Error (e.ToString()) }

let readJson<'a> stream =
    let mutable options = Json.JsonSerializerOptions()
    options.AllowTrailingCommas <- true
    try
        task {
            let! a = (Json.JsonSerializer.DeserializeAsync<'a>(stream, options)).AsTask()
            return Ok a
        }
    with
    e -> task { return Error (e.ToString()) }

let readNewtonsoft<'a> (stream: IO.Stream) =
    let serializer = JsonSerializer()
    use tr = new StreamReader(stream) // StreamReader will dispose the stream
    use jtr = new JsonTextReader(tr, DateParseHandling = DateParseHandling.None)

    try
        task {
            let a = (serializer.Deserialize<'a> jtr)
            return Ok a
        }
    with
    e -> task { return Error (e.ToString()) }

let jsonReader<'a, 'r, 'err> (reader: Stream -> Task<Result<'a, string>>) (next: NextFunc<'a,'r, 'err>) (context: HttpContext) : HttpFuncResult<'r, 'err> =
    task {
        use! stream = context.Response.Content.ReadAsStreamAsync ()
        let! ret = reader stream

        match ret with
        | Ok result ->
            return! next { Request = context.Request; Response = result }
        | Error error ->
            return Error (Panic <| JsonDecodeException error)
    }

let get2 (reader: Stream -> Task<Result<ResizeArray<TestType2>, string>>) : HttpHandler<HttpResponseMessage, ResizeArray<TestType2>, 'b, TestError> =
    GET
    >=> setUrl "http://test"
    >=> fetch
    >=> withError decodeError
    >=> jsonReader reader
