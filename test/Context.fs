module Tests.Context

open System.Net.Http
open System.Threading

open Oryx
open FsCheck.Xunit
open Xunit

[<Property>]
let ``Adding a header to a context creates a context that contains that header`` header =
    task {
        let ctx =
            httpRequest
            |> withHeader header
            |> ask
            |> map
                (fun ctx ->
                    ctx.Request.Headers.TryGetValue(fst header)
                    |> (fun (found, value) -> found && value = snd header))

        let! result = ctx |> runUnsafeAsync
        return result
    }
    |> fun x -> x.Result

[<Property>]
let ``Adding two headers to a context creates a context that contains both headers`` h1 h2 =
    task {
        let ctx =
            httpRequest
            |> withHeader h1
            |> withHeader h2
            |> ask
            |> map
                (fun ctx ->
                    let p2 =
                        ctx.Request.Headers.TryGetValue(fst h2)
                        |> (fun (found, value) -> found && value = snd h2)

                    let p1 =
                        if (fst h1 = fst h2) then
                            true
                        else
                            ctx.Request.Headers.TryGetValue(fst h1)
                            |> (fun (found, value) -> found && value = snd h1)

                    p1 && p2)

        let! result = ctx |> runUnsafeAsync
        return result
    }
    |> fun x -> x.Result

[<Property>]
let ``Adding a bearer token to a context creates a context with that token`` token =
    task {
        let ctx =
            httpRequest
            |> withBearerToken token
            |> ask
            |> map
                (fun ctx ->
                    ctx.Request.Headers.TryGetValue "Authorization"
                    |> (fun (found, value) -> found && value = (sprintf "Bearer %s" token)))

        let! result = ctx |> runUnsafeAsync
        return result
    }
    |> fun x -> x.Result

[<Property>]
let ``Adding http client creates a context with that http client`` () =
    task {
        let client = new HttpClient()

        let ctx =
            httpRequest
            |> withHttpClient client
            |> ask
            |> map (fun ctx -> ctx.Request.HttpClient() = client)

        let! result = ctx |> runUnsafeAsync
        return result
    }
    |> fun x -> x.Result

[<Property>]
let ``Adding http client factory creates a context with that http client`` () =
    task {
        let client = new HttpClient()

        let ctx =
            httpRequest
            |> withHttpClientFactory (fun () -> client)
            |> ask
            |> map (fun ctx -> ctx.Request.HttpClient() = client)

        let! result = ctx |> runUnsafeAsync
        return result
    }
    |> fun x -> x.Result

[<Property>]
let ``Adding url builder creates a context with that url builder`` () =
    task {
        let urlBuilder = fun (req: HttpRequest) -> "test"

        let ctx =
            httpRequest
            |> withUrlBuilder urlBuilder
            |> ask
            |> map (fun ctx -> ctx.Request.UrlBuilder ctx.Request = "test")

        let! result = ctx |> runUnsafeAsync
        return result
    }
    |> fun x -> x.Result

[<Property>]
let ``Adding cancellation token creates a context with that cancellation token`` () =
    task {
        let cancellationToken = CancellationToken.None

        let ctx =
            httpRequest
            |> withCancellationToken cancellationToken
            |> ask
            |> map (fun ctx -> ctx.Request.CancellationToken = cancellationToken)

        let! result = ctx |> runUnsafeAsync
        return result
    }
    |> fun x -> x.Result
