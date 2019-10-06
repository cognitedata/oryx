module Tests.Context

open System.IO

open Xunit
open Swensen.Unquote

open Tests.Common

open Oryx
open FsCheck.Xunit


[<Property>]
let ``Adding a header to a context creates a context that contains that header`` header =
    let ctx = Context.defaultContext |> Context.addHeader header
    List.contains header ctx.Request.Headers

[<Property>]
let ``Adding two headers to a context creates a context that contains both headers`` h1 h2 =
    let ctx =
        Context.defaultContext
        |> Context.addHeader h1
        |> Context.addHeader h2

    let p1 = List.contains h1 ctx.Request.Headers
    let p2 = List.contains h2 ctx.Request.Headers
    p1 && p2
