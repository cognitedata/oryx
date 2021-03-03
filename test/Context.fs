module Tests.Context

open System.Net.Http
open System.Threading

open Oryx
open FsCheck.Xunit

[<Property>]
let ``Adding a header to a context creates a context that contains that header`` header =
    let ctx =
        HttpContext.defaultContext
        |> HttpContext.withHeader header

    ctx.Request.Headers.TryGetValue(fst header)
    |> (fun (found, value) -> found && value = snd header)

[<Property>]
let ``Adding two headers to a context creates a context that contains both headers`` h1 h2 =
    let ctx =
        HttpContext.defaultContext
        |> HttpContext.withHeader h1
        |> HttpContext.withHeader h2

    let p2 =
        ctx.Request.Headers.TryGetValue(fst h2)
        |> (fun (found, value) -> found && value = snd h2)

    let p1 =
        if (fst h1 = fst h2) then
            true
        else
            ctx.Request.Headers.TryGetValue(fst h1)
            |> (fun (found, value) -> found && value = snd h1)

    p1 && p2

[<Property>]
let ``Adding a bearer token to a context creates a context with that token`` token =
    let ctx =
        HttpContext.defaultContext
        |> HttpContext.withBearerToken token

    ctx.Request.Headers.TryGetValue "Authorization"
    |> (fun (found, value) -> found && value = (sprintf "Bearer %s" token))

[<Property>]
let ``Adding http client creates a context with that http client`` () =
    let client = new HttpClient()

    let ctx =
        HttpContext.defaultContext
        |> HttpContext.withHttpClient client

    ctx.Request.HttpClient() = client

[<Property>]
let ``Adding http client factory creates a context with that http client`` () =
    let client = new HttpClient()

    let ctx =
        HttpContext.defaultContext
        |> HttpContext.withHttpClientFactory (fun () -> client)

    ctx.Request.HttpClient() = client

[<Property>]
let ``Adding url builder creates a context with that url builder`` () =
    let urlBuilder = fun (req: HttpRequest) -> "test"

    let ctx =
        HttpContext.defaultContext
        |> HttpContext.withUrlBuilder urlBuilder

    ctx.Request.UrlBuilder ctx.Request = "test"

[<Property>]
let ``Adding cancellation token creates a context with that cancellation token`` () =
    let cancellationToken = CancellationToken.None

    let ctx =
        HttpContext.defaultContext
        |> HttpContext.withCancellationToken cancellationToken

    ctx.Request.CancellationToken = cancellationToken
