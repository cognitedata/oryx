module Tests.Fetch

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open Microsoft.Extensions.Logging

open FSharp.Control.TaskBuilder
open Swensen.Unquote
open Xunit

open Oryx

open Tests
open Tests.Common

[<Fact>]
let ``Get with return expression is Ok`` () =
    task {
        // Arrange
        let mutable retries = 0
        let json = """42"""

        let stub =
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(fun request token ->
                (task {
                    retries <- retries + 1
                    let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
                    responseMessage.Content <- new StringContent(json)
                    responseMessage.Headers.Add("x-request-id", "123")
                    return responseMessage
                }))

        let client = new HttpClient(new HttpMessageHandlerStub(stub))

        let ctx =
            httpRequest
            |> withHttpClient client
            |> withUrlBuilder (fun _ -> "http://test.org/")
            |> withHeader ("api-key", "test-key")

        // Act
        let request =
            http {
                let! result = ctx |> get
                return result + 1
            }

        let! result = request |> runAsync
        let retries' = retries

        match result with
        | Ok response -> test <@ response = 43 @>
        | _ -> ()

        // Assert
        test <@ Result.isOk result @>
        test <@ retries' = 1 @>
    }

[<Fact>]
let ``Post url encoded with return expression is Ok`` () =
    task {
        // Arrange
        let json = """{ "value": 42 }"""
        let mutable urlencoded = ""

        let stub =
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(fun request token ->
                task {
                    let! content = request.Content.ReadAsStringAsync()
                    urlencoded <- content
                    let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
                    responseMessage.Content <- new StringContent(json)
                    return responseMessage
                })

        let client = new HttpClient(new HttpMessageHandlerStub(stub))

        let ctx =
            httpRequest
            |> withHttpClient client
            |> withUrlBuilder (fun _ -> "http://test.org/")
            |> withHeader ("api-key", "test-key")

        let query = Seq.singleton ("foo", "bar")
        let content = FormUrlEncodedContent.FromTuples query

        // Act
        let request =
            http {
                let! result = ctx |> post (fun _ -> content)
                return result
            }

        let! result = request |> runAsync
        let urldecoded' = urlencoded

        // Assert
        test <@ Result.isOk result @>
        test <@ urldecoded'.Contains "foo=bar" @>
    }

[<Fact>]
let ``Get with logging is OK`` () =
    task {
        // Arrange
        let metrics = TestMetrics()
        let logger = new TestLogger<string>()
        let msg = "custom message"

        let stub =
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(fun request token ->
                (task {
                    let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
                    responseMessage.Content <- new PushStreamContent("42")
                    responseMessage.Headers.Add("test", value = "test-value")
                    responseMessage.Headers.Add("test2", value = "not-included-in-log")
                    responseMessage.Headers.Add("X-Request-ID", value = "test-request-id")
                    return responseMessage
                }))

        let client = new HttpClient(new HttpMessageHandlerStub(stub))

        let logPlaceholders =
            String.Join(
                "\n← ",
                [| "ResponseHeader[test]"
                   "Message"
                   "ResponseHeader[missing-key]"
                   "ResponseHeader[X-Request-ID]"
                   "ResponseHeader[x-request-id]"
                   "ResponseHeader"
                   "ResponseHeader["
                   "ResponseHeader]"
                   "ResponseHeader[[]]" |]
                |> Array.map (fun x -> $"{{{x}}}")
            )

        let ctx =
            httpRequest
            |> withHttpClient client
            |> withUrlBuilder (fun _ -> "http://test.org/")
            |> withHeader ("api-key", "test-key")
            |> withLogMessage (msg)
            |> withMetrics metrics
            |> withLogger logger
            |> withLogLevel LogLevel.Debug
            |> withLogFormat (HttpContext.defaultLogFormat + $"\n← {logPlaceholders}\n← end")
            |> cache

        // Act
        let request =
            http {
                let! result = ctx |> get
                return result + 2
            }

        let! result = request |> runAsync

        // Assert
        test <@ logger.Output.Contains "42" @>
        test <@ logger.Output.Contains "http://test.org" @>

        test
            <@
                logger.Output.Contains
                    $"test-value\n← {msg}\n← \n← test-request-id\n← test-request-id\n← \n← \n← \n← \n← end"
            @>

        test <@ logger.Output.Contains "not-included-in-log" = false @>
        test <@ Result.isOk result @>
        test <@ metrics.Retries = 0L @>
        test <@ metrics.Fetches = 1L @>
        test <@ metrics.Errors = 0L @>
    }

[<Fact>]
let ``Post with logging is OK`` () =
    task {
        // Arrange
        let mutable retries = 0
        let logger = new TestLogger<string>()
        let json = """{ "ping": 42 }"""
        let msg = "custom message"

        let stub =
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(fun request token ->
                (task {
                    retries <- retries + 1
                    let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
                    responseMessage.Content <- new PushStreamContent("""{ "pong": 42 }""")
                    responseMessage.Headers.Add("test", value = "test-value")
                    responseMessage.Headers.Add("test2", value = "not-included-in-log")
                    responseMessage.Headers.Add("X-Request-ID", value = "test-request-id")
                    return responseMessage
                }))

        let client = new HttpClient(new HttpMessageHandlerStub(stub))

        let content () =
            new StringableContent(json) :> HttpContent

        let logPlaceholders =
            String.Join(
                "\n← ",
                [| "ResponseHeader[test]"
                   "Message"
                   "ResponseHeader[missing-key]"
                   "ResponseHeader[X-Request-ID]"
                   "ResponseHeader[x-request-id]"
                   "ResponseHeader"
                   "ResponseHeader["
                   "ResponseHeader]"
                   "ResponseHeader[[]]" |]
                |> Array.map (fun x -> $"{{{x}}}")
            )

        let ctx =
            httpRequest
            |> withHttpClientFactory (fun () -> client)
            |> withUrlBuilder (fun _ -> "http://testing.org/")
            |> withHeader ("api-key", "test-key")
            |> withLogger (logger)
            |> withLogLevel LogLevel.Debug
            |> withLogFormat (HttpContext.defaultLogFormat + $"\n← {logPlaceholders}\n← end")
            |> cache

        // Act
        let! result = ctx |> withLogMessage msg |> post content |> runAsync

        let retries' = retries

        // Assert
        test <@ logger.Output.Contains json @>
        test <@ logger.Output.Contains msg @>
        test <@ logger.Output.Contains "http://testing.org" @>

        test
            <@
                logger.Output.Contains
                    $"test-value\n← {msg}\n← \n← test-request-id\n← test-request-id\n← \n← \n← \n← \n← end"
            @>

        test <@ logger.Output.Contains "not-included-in-log" = false @>
        test <@ Result.isOk result @>
        test <@ retries' = 1 @>
    }

[<Fact>]
let ``Multiple post with logging is OK`` () =
    task {
        // Arrange
        let logger = new TestLogger<string>()
        let json x = sprintf """{ "ping": %d }""" x

        let stub =
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(fun request token ->
                (task {
                    let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
                    responseMessage.Content <- new PushStreamContent("42")
                    return responseMessage
                }))

        let client = new HttpClient(new HttpMessageHandlerStub(stub))

        let content x () =
            new StringableContent(json x) :> HttpContent


        let ctx =
            httpRequest
            |> withHttpClientFactory (fun () -> client)
            |> withUrlBuilder (fun _ -> "http://testing.org/")
            |> withHeader ("api-key", "test-key")
            |> withLogger (logger)
            |> withLogLevel LogLevel.Debug

        // Act
        let! result =
            http {
                let! a = ctx |> withLogMessage "first" |> post (content 41)

                let! b = ctx |> withLogMessage "second" |> post (content 42)

                return a + b
            }
            |> runAsync

        // Assert
        test <@ Result.isOk result @>
        test <@ logger.Output.Contains(json 41) @>
        test <@ logger.Output.Contains "first" @>
        test <@ logger.Output.Contains "http://testing.org" @>
    }

[<Fact>]
let ``Post with disabled logging does not log`` () =
    task {
        // Arrange
        let mutable retries = 0
        let logger = new TestLogger<string>()
        let json = """{ "value": 42 }"""
        let msg = "custom message"

        let stub =
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(fun request token ->
                (task {
                    retries <- retries + 1
                    let responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
                    responseMessage.Content <- new PushStreamContent(json)
                    return responseMessage
                }))

        let client = new HttpClient(new HttpMessageHandlerStub(stub))

        let content () =
            new StringableContent(json) :> HttpContent

        let ctx =
            httpRequest
            |> withHttpClient client
            |> withUrlBuilder (fun _ -> "http://test.org/")
            |> withHeader ("api-key", "test-key")
            |> withLogger logger
            |> withLogLevel LogLevel.None
            |> cache

        // Act
        let request =
            http {
                let! result = ctx |> withLogMessage msg |> post content
                return result
            }

        let! result = request |> runAsync
        let retries' = retries

        // Assert
        test <@ logger.Output = "" @>
        test <@ Result.isOk result @>
        test <@ retries' = 1 @>
    }

[<Fact>]
let ``Fetch with internal error will log error`` () =
    task {
        // Arrange
        let json = """{ "code": 500, "message": "failed" }"""
        let logger = new TestLogger<string>()

        let stub =
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(fun request token ->
                (task {
                    let responseMessage = new HttpResponseMessage(HttpStatusCode.InternalServerError)

                    responseMessage.Content <- new StringableContent(json)
                    return responseMessage
                }))

        let client = new HttpClient(new HttpMessageHandlerStub(stub))

        let ctx =
            httpRequest
            |> withHttpClient client
            |> withUrlBuilder (fun _ -> "http://test.org/")
            |> withHeader ("api-key", "test-key")
            |> withLogger logger
            |> withLogLevel LogLevel.Debug
            |> cache

        // Act
        let request =
            let content = fun () -> new PushStreamContent("testing") :> HttpContent

            http {
                let! result = ctx |> post content
                return result
            }

        let! result = request |> runAsync

        // Assert
        test <@ Result.isError result @>
        test <@ logger.Output.Contains "Got error" @>
    }
