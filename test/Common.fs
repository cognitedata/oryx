module Tests.Common

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.V2

open Oryx

type HttpMessageHandlerStub (sendAsync: Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) =
    inherit HttpMessageHandler ()
    let sendAsync = sendAsync

    override self.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) : Task<HttpResponseMessage> =
        task {
            return! sendAsync.Invoke(request, cancellationToken)
        }

let unit (value: 'a) (next: NextFunc<'a, 'b>) (context: HttpContext) : Task<Result<Context<'b>, ResponseError>> =
    next { Request=context.Request; Response = value }

let add (a: int) (b: int) (next: NextFunc<int, 'b>) (context: HttpContext) : Task<Result<Context<'b>, ResponseError>>  =
    unit (a + b) next context

let error msg (next: NextFunc<'a, 'b>) (context: Context<'a>) : Task<Result<Context<'b>, ResponseError>> =
    Error { ResponseError.empty with Message=msg } |> Task.FromResult

