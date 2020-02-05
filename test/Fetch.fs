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

open Tests.Common

[<Fact>]
let ``Get with return expression is Ok``() = task {
    // Arrange
    let mutable retries = 0
    let json = """{ "value": 42 }"""

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
    let request = req {
        let! result = get ()
        return result
    }

    let! result = request |> runAsync ctx
    let retries' = retries

    // Assert
    test <@ Result.isOk result @>
    test <@ retries' = 1 @>
}

[<Fact>]
let ``Post url encoded with return expression is Ok``() = task {
    // Arrange
    let json = """{ "value": 42 }"""
    let mutable urlencoded = ""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        task {
            let! content = request.Content.ReadAsStringAsync ()
            urlencoded <- content
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new StringContent(json)
            return responseMessage
        })

    let client = new HttpClient(new HttpMessageHandlerStub(stub))

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.setUrlBuilder (fun _ -> "http://test.org/")
        |> Context.addHeader ("api-key", "test-key")

    let query = Seq.singleton ("foo", "bar")
    let content = FormUrlEncodedContent.FromTuples query

    // Act
    let request = req {
        let! result = post (fun _ -> content)
        return result
    }

    let! result = request |> runAsync ctx
    let urldecoded' = urlencoded

    // Assert
    test <@ Result.isOk result @>
    test <@ urldecoded'.Contains "foo=bar" @>
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
    let request =
        req {
            let! result = retry >=> get ()
            return result
        }

    let! result = request |> runAsync ctx
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
    let request =
        let content = new PushStreamContent("testing")
        req {
            let! result = retry >=> post (fun _ -> new PushStreamContent("testing") :> _)
            return result
        }

    let! result = request |> runAsync ctx
    let retries' = retries

    // Assert
    test <@ Result.isError result @>
    test <@ retries' = 6 @>
}

[<Fact>]
let ``Get with logging response is OK``() = task {
    // Arrange
    let mutable retries = 0
    let logger = new TestLogger<string>()
    let json = """{ "value": 42 }"""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            retries <- retries + 1
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new PushStreamContent(json)
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.setUrlBuilder (fun _ -> "http://test.org/")
        |> Context.addHeader ("api-key", "test-key")
        |> Context.setLogger(logger)

    // Act
    let request = req {
        let! result = get () >=> logResponse
        return result
    }

    let! result = request |> runAsync ctx
    let retries' = retries

    // Assert
    test <@ logger.Output = json @>
    test <@ Result.isOk result @>
    test <@ retries' = 1 @>
}

[<Fact>]
let ``Get with logging request is OK``() = task {
    // Arrange
    let mutable retries = 0
    let logger = new TestLogger<string>()
    let json = """{ "value": 42 }"""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            retries <- retries + 1
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new PushStreamContent("")
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))
    let content () = new StringContent(json) :> HttpContent

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.setUrlBuilder (fun _ -> "http://test.org/")
        |> Context.addHeader ("api-key", "test-key")
        |> Context.setLogger(logger)


    // Act
    let request = req {
        let! result = post content >=> logRequest
        return result
    }

    let! result = request |> runAsync ctx
    let retries' = retries

    // Assert
    test <@ logger.Output = json @>
    test <@ Result.isOk result @>
    test <@ retries' = 1 @>
}