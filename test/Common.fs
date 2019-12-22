module Tests.Common

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.V2
open Thoth.Json.Net

open Oryx
open Oryx.ResponseReaders
open Oryx.Retry
open System.Net

type HttpMessageHandlerStub (sendAsync: Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) =
    inherit HttpMessageHandler ()
    let sendAsync = sendAsync

    override self.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) : Task<HttpResponseMessage> =
        task {
            return! sendAsync.Invoke(request, cancellationToken)
        }

let unit (value: 'a) (next: NextFunc<'a, 'r, 'err>) (context: HttpContext) : HttpFuncResult<'r, 'err> =
    next { Request=context.Request; Response = value }

let add (a: int) (b: int) (next: NextFunc<int, 'b, 'err>) (context: HttpContext) : HttpFuncResult<'b, 'err>  =
    unit (a + b) next context

exception TestException of string
    with override this.ToString () =
            this.Data0

type TestError = {
    Code : int
    Message : string
}

let apiError msg (next: NextFunc<'b, 'c, 'err>) (_: Context<'a>) : HttpFuncResult<'c, TestError> =
    { Code = 400; Message = msg } |> ResponseError |> Error |> Task.FromResult

let error msg (next: NextFunc<'b, 'c, 'err>) (_: Context<'a>) : HttpFuncResult<'c, TestError> =
   TestException msg |> Panic |> Error |> Task.FromResult


/// A bad request handler to use with the `catch` handler. It takes a response to return as Ok.
let badRequestHandler<'a, 'b> (response: 'b) (error: HandlerError<TestError>) (ctx : Context<'a>) = task {
    match error with
    | ResponseError api ->
        match enum<HttpStatusCode>(api.Code) with
        | HttpStatusCode.BadRequest -> return Ok { Request = ctx.Request; Response = response }
        | _ -> return Error error
    | _ ->
        return Error error
}

let shouldRetry (error: HandlerError<TestError>) : bool =
    match error with
    | ResponseError error -> true
    | Panic _ -> false

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
    let decoder : Decoder<_> = Decode.object (fun get -> {| Value = get.Required.Field "value" Decode.int |})

    GET
    >=> setUrl "http://test"
    >=> fetch
    >=> withError decodeError
    >=> json decoder

let post content =
    let decoder : Decoder<_> = Decode.object (fun get -> {| Value = get.Required.Field "value" Decode.int |})

    POST
    >=> setUrl "http://test"
    >=> setContent content
    >=> fetch
    >=> withError decodeError
    >=> json decoder

let retry next ctx = retry shouldRetry 0<ms> 5 next ctx