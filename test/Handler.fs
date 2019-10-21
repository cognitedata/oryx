module Tests.Handler

open System.Threading.Tasks

open FSharp.Control.Tasks.V2
open Swensen.Unquote
open Xunit
open FsCheck.Xunit
open FsCheck
open FsCheck.Arb


open Oryx
open Tests.Common

[<Fact>]
let ``Simple unit handler is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext

    // Act
    let! result = unit 42 finishEarly ctx

    // Assert
    test <@ Result.isOk result @>
    match result with
    | Ok ctx -> test <@ ctx.Response = 42 @>
    | Error err -> failwith err.Message
}

[<Fact>]
let ``Simple error handler is Error``() = task {
    // Arrange
    let ctx = Context.defaultContext

    // Act
    let! result = error "failed" finishEarly ctx

    // Assert
    test <@ Result.isError result @>
    match result with
    | Ok _ -> failwith "error"
    | Error err -> test <@ err.Message = "failed" @>
}

[<Fact>]
let ``Simple error then ok is Error``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let req = error "failed" >=> unit 42

    // Act
    let! result = req finishEarly ctx

    // Assert
    test <@ Result.isError result @>
    match result with
    | Ok _ -> failwith "error"
    | Error err -> test <@ err.Message = "failed" @>
}

[<Fact>]
let ``Simple ok then error is Error``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let req = unit 42 >=> error "failed"

    // Act
    let! result = req finishEarly ctx

    // Assert
    test <@ Result.isError result @>
    match result with
    | Ok _ -> failwith "error"
    | Error err -> test <@ err.Message = "failed" @>
}

[<Fact>]
let ``Sequential handlers is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let req = sequential [
            unit 1
            unit 2
            unit 3
            unit 4
            unit 5
    ]

    // Act
    let! result = req finishEarly ctx

    // Assert
    test <@ Result.isOk result @>
    match result with
    | Ok ctx -> test <@ ctx.Response = [1; 2; 3; 4; 5] @>
    | Error err -> failwith "error"
}

[<Fact>]
let ``Concurrent handlers is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let req = concurrent [
        unit 1
        unit 2
        unit 3
        unit 4
        unit 5
    ]

    // Act
    let! result = req finishEarly ctx

    // Assert
    test <@ Result.isOk result @>
    match result with
    | Ok ctx -> test <@ ctx.Response = [1; 2; 3; 4; 5] @>
    | Error err -> failwith "error"
}

[<Property>]
let ``Chunked handlers is Ok`` (PositiveInt chunkSize) (PositiveInt maxConcurrency) =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let req = Chunk.chunk chunkSize maxConcurrency unit [1; 2; 3; 4; 5]

        // Act
        let! result = req finishEarly ctx

        // Assert
        test <@ Result.isOk result @>
        match result with
        | Ok ctx -> test <@ Seq.toList ctx.Response = [ 1; 2; 3; 4; 5 ] @>
        | Error err -> failwith "error"
    } |> fun x -> x.Result
