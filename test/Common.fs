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
