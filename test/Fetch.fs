module Tests.Fetch

open System
open System.Net.Http
open System.Threading
open System.Net
open System.Threading.Tasks

open FSharp.Control.Tasks.V2
open Swensen.Unquote
open Xunit

open Oryx
open Oryx.Retry

open Tests.Common

[<Fact>]
let ``Get asset with fusion return expression is Ok``() = task {
    // Arrange
    let mutable retries = 0
    let json = """{ "value": 42}"""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            retries <- retries + 1
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new StringContent(json)
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.setUrlBuilder (fun _ -> "http://test.org/")
        |> Context.addHeader ("api-key", "test-key")

    // Act
    let req = oryx {
        let! result = get ()
        return result
    }

    let! result = runHandler req ctx
    let retries' = retries

    // Assert
    test <@ Result.isOk result @>
    test <@ retries' = 1 @>
}

[<Fact>]
let ``Fetch with retry is Ok``() = task {
    // Arrange
    let mutable retries = 0
    let json = """{ "value": 42}"""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            retries <- retries + 1
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new StringContent(json)
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.setUrlBuilder (fun _ -> "http://test.org/")
        |> Context.addHeader ("api-key", "test-key")

    // Act
    let req =
        oryx {
            let! result = get ()
            return result
        } |> retry shouldRetry 0<ms> 5

    let! result = runHandler req ctx
    let retries' = retries

    // Assert
    test <@ Result.isOk result @>
    test <@ retries' = 1 @>
}

[<Fact>]
let ``Fetch with retry on internal error will retry``() = task {
    // Arrange
    let mutable retries = 0
    let json = """{ "code": 500, "message": "failed" }"""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            retries <- retries + 1
            let responseMessage = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            responseMessage.Content <- new StringContent(json)
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.setUrlBuilder (fun _ -> "http://test.org/")
        |> Context.addHeader ("api-key", "test-key")

    // Act
    let req =
        oryx {
            let! result = get ()
            return result
        } |> retry shouldRetry 0<ms> 5

    let! result = runHandler req ctx
    let retries' = retries

    // Assert
    test <@ Result.isError result @>
    test <@ retries' = 6 @>
}