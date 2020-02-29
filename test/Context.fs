module Tests.Context

open System.Net.Http
open System.Threading

open Oryx
open FsCheck.Xunit

[<Property>]
let ``Adding a header to a context creates a context that contains that header`` header =
    let ctx = Context.defaultContext |> Context.withHeader header
    List.contains header ctx.Request.Headers

[<Property>]
let ``Adding two headers to a context creates a context that contains both headers`` h1 h2 =
    let ctx =
        Context.defaultContext
        |> Context.withHeader h1
        |> Context.withHeader h2

    let p1 = List.contains h1 ctx.Request.Headers
    let p2 = List.contains h2 ctx.Request.Headers
    p1 && p2

[<Property>]
let ``Adding a bearer token to a context creates a context with that token`` token =
    let ctx = Context.defaultContext |> Context.withBearerToken token
    ctx.Request.Headers
    |> List.exists (fun (header, value) ->
        header = "Authorization" && value = (sprintf "Bearer %s" token))

[<Property>]
let ``Adding http client creates a context with that http client`` () =
    let client = new HttpClient()
    let ctx = Context.defaultContext |> Context.setHttpClient client
    ctx.Request.HttpClient () = client

[<Property>]
let ``Adding url builder creates a context with that url builder`` () =
    let urlBuilder = fun (req: HttpRequest) -> "test"
    let ctx = Context.defaultContext |> Context.withUrlBuilder urlBuilder
    ctx.Request.UrlBuilder ctx.Request = "test"

[<Property>]
let ``Adding cancellation token creates a context with that cancellation token`` () =
    let cancellationToken = CancellationToken.None
    let ctx = Context.defaultContext |> Context.withCancellationToken cancellationToken
    ctx.Request.CancellationToken = Some cancellationToken
