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
    | Error (Panic err) -> raise err
    | Error (ApiError err) -> failwith (err.ToString())
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
    | Error (Panic err) -> test <@ err.ToString() = "failed" @>
    | Error (ApiError err) -> failwith (err.ToString())
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
    | Error (Panic err) -> test <@ err.ToString() = "failed" @>
    | Error (ApiError err) -> failwith (err.ToString())
}

[<Fact>]
let ``Simple ok then error is Error``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let req = unit 42 >=> error "failed"

    // Act
    let! result = req finishEarly ctx

    // Assert
    match result with
    | Ok _ -> failwith "error"
    | Error (Panic err) -> test <@ err.ToString () = "failed" @>
    | Error (ApiError err) -> failwith (err.ToString())
}

[<Fact>]
let ``Catching ok is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let errorHandler = badRequestHandler 420
    let req = unit 42 >=> catch errorHandler >=> map (fun a -> a * 10)

    // Act
    let! result = req finishEarly ctx

    // Assert
    match result with
    | Ok ctx -> test <@ ctx.Response = 420 @>
    | Error (Panic err) -> raise err
    | Error (ApiError err) -> failwith (err.ToString())
}

[<Fact>]
let ``Catching errors is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let errorHandler = badRequestHandler 420
    let req = unit 42 >=> catch errorHandler >=> apiError "failed"

    // Act
    let! result = req finishEarly ctx

    // Assert
    match result with
    | Ok ctx -> test <@ ctx.Response = 420 @>
    | Error (Panic err) -> raise err
    | Error (ApiError err) -> failwith (err.ToString())
}

[<Fact>]
let ``Not catching errors is Error``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let errorHandler = badRequestHandler 420
    let req = unit 42 >=> catch (fun ctx next -> Task.FromResult (Error ctx)) >=> error "failed"

    // Act
    let! result = req finishEarly ctx

    // Assert
    match result with
    | Ok _ -> failwith "error"
    | Error (Panic err) -> test <@ err.ToString () = "failed" @>
    | Error (ApiError err) -> failwith (err.ToString())
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
let ``Sequential handlers with an Error is Error``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let req = sequential [
            unit 1
            unit 2
            error "fail"
            unit 4
            unit 5
    ]

    // Act
    let! result = req finishEarly ctx

    // Assert
    test <@ Result.isError result @>
    match result with
    | Ok _ -> failwith "expected failure"
    | Error (Panic err) -> test <@ err.ToString () = "fail" @>
    | Error (ApiError err) -> failwith (err.ToString())
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

[<Fact>]
let ``Concurrent handlers with an Error is Error``() = task {
    // Arrange
    let ctx = Context.defaultContext
    let req = concurrent [
            unit 1
            unit 2
            error "fail"
            unit 4
            unit 5
    ]

    // Act
    let! result = req finishEarly ctx

    // Assert
    test <@ Result.isError result @>
    match result with
    | Ok _ -> failwith "expected failure"
    | Error (Panic err) -> test <@ err.ToString () = "fail" @>
    | Error (ApiError err) -> failwith (err.ToString())
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
