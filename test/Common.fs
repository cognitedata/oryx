module Tests.Common

open System.IO

open Xunit
open Swensen.Unquote

open System
open System.Net
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

[<RequireQualifiedAccess>]
module Result =
    let isOk = function
        | Ok _ -> true
        | Error _ -> false

    let isError res = not (isOk res)

let unit (value: 'a) (next: NextFunc<'a, 'b>) (context: HttpContext) : Task<Context<'b>> =
    next { Request=context.Request; Result = Ok value }

let add (a: int) (b: int) (next: NextFunc<int, 'b>) (context: HttpContext) : Task<Context<'b>> =
    unit (a + b) next context

let error msg (next: NextFunc<'a, 'b>) (context: Context<'a>) : Task<Context<'b>> =
    Task.FromResult { Request=context.Request; Result = { ResponseError.empty with Message=msg } |> Error }

