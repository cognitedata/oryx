module Tests.Fetch

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open Microsoft.Extensions.Logging

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
        |> Context.withUrlBuilder (fun _ -> "http://test.org/")
        |> Context.withHeader ("api-key", "test-key")

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
        |> Context.withUrlBuilder (fun _ -> "http://test.org/")
        |> Context.withHeader ("api-key", "test-key")

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
    let metrics = TestMetrics ()
    let json = """{ "value": 42 }"""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new StringContent(json)
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.withUrlBuilder (fun _ -> "http://test.org/")
        |> Context.withHeader ("api-key", "test-key")
        |> Context.withMetrics metrics

    // Act
    let request =
        req {
            let! result = retry >=> get ()
            return result
        }

    let! result = request |> runAsync ctx

    // Assert
    test <@ Result.isOk result @>
    test <@ metrics.Retries = 0L @>
    test <@ metrics.Fetches = 1L @>
    test <@ metrics.Errors = 0L @>
}

[<Fact>]
let ``Fetch with retry on internal error will retry``() = task {
    // Arrange
    let metrics = TestMetrics ()
    let json = """{ "code": 500, "message": "failed" }"""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            let responseMessage = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            responseMessage.Content <- new StringableContent(json)
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.withUrlBuilder (fun _ -> "http://test.org/")
        |> Context.withHeader ("api-key", "test-key")
        |> Context.withMetrics metrics

    // Act
    let request =
        let content = new PushStreamContent("testing")
        req {
            let! result = retry >=> post (fun _ -> new PushStreamContent("testing") :> HttpContent)
            return result
        }

    let! result = request |> runAsync ctx

    // Assert
    test <@ Result.isError result @>
    test <@ metrics.Retries = int64 retryCount @>
    test <@ metrics.Fetches = int64 retryCount + 1L @>
    test <@ metrics.Errors = int64 retryCount + 1L @>}

[<Fact>]
let ``Get with logging is OK``() = task {
    // Arrange
    let metrics = TestMetrics ()
    let logger = new TestLogger<string>()
    let json = """{ "value": 42 }"""

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new PushStreamContent(json)
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.withUrlBuilder (fun _ -> "http://test.org/")
        |> Context.withHeader ("api-key", "test-key")
        |> Context.withMetrics metrics
        |> Context.withLogger logger
        |> Context.withLogLevel LogLevel.Debug

    // Act
    let request = req {
        let! result = get () >=> log
        return result
    }

    let! result = request |> runAsync ctx

    // Assert
    test <@ logger.Output.Contains json @>
    test <@ Result.isOk result @>
    test <@ metrics.Retries = 0L @>
    test <@ metrics.Fetches = 1L @>
    test <@ metrics.Errors = 0L @>
}

[<Fact>]
let ``Post with logging is OK``() = task {
    // Arrange
    let mutable retries = 0
    let logger = new TestLogger<string>()
    let json = """{ "ping": 42 }"""
    let msg = "custom message"

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            retries <- retries + 1
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new PushStreamContent("""{ "pong": 42 }""")
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))
    let content () = new StringableContent(json) :> HttpContent

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.withUrlBuilder (fun _ -> "http://test.org/")
        |> Context.withHeader ("api-key", "test-key")
        |> Context.withLogger(logger)
        |> Context.withLogLevel LogLevel.Debug


    // Act
    let request = req {
        let! result = post content >=> logWithMessage msg
        return result
    }

    let! result = request |> runAsync ctx
    let retries' = retries

    // Assert
    test <@ logger.Output.Contains json @>
    test <@ logger.Output.Contains msg @>
    test <@ Result.isOk result @>
    test <@ retries' = 1 @>
}

[<Fact>]
let ``Post with disabled logging does not log``() = task {
    // Arrange
    let mutable retries = 0
    let logger = new TestLogger<string>()
    let json = """{ "value": 42 }"""
    let msg = "custom message"

    let stub =
        Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>(fun request token ->
        (task {
            retries <- retries + 1
            let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
            responseMessage.Content <- new PushStreamContent("")
            return responseMessage
        }))

    let client = new HttpClient(new HttpMessageHandlerStub(stub))
    let content () = new StringableContent(json) :> HttpContent

    let ctx =
        Context.defaultContext
        |> Context.setHttpClient client
        |> Context.withUrlBuilder (fun _ -> "http://test.org/")
        |> Context.withHeader ("api-key", "test-key")
        |> Context.withLogger logger
        |> Context.withLogLevel LogLevel.None

    // Act
    let request = req {
        let! result = post content >=> logWithMessage msg
        return result
    }

    let! result = request |> runAsync ctx
    let retries' = retries

    // Assert
    test <@ logger.Output = "" @>
    test <@ Result.isOk result @>
    test <@ retries' = 1 @>
}