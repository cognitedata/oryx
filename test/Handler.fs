module Tests.Handler

open System.Threading.Tasks

open FSharp.Control.Tasks.V2
open Swensen.Unquote
open Xunit
open FsCheck.Xunit
open FsCheck
open FsCheck.Arb


open Oryx
open Oryx.Chunk
open Tests.Common
open System

[<Fact>]
let ``Simple unit handler is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext

        // Act
        let! content = unit 42 >=> unit 43 |> runUnsafeAsync ctx

        // Assert
        test <@ content = 43 @>
    }

[<Fact>]
let ``Simple error handler is Error`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext

        // Act
        let! result = error "failed" |> runAsync ctx

        // Assert
        test <@ Result.isError result @>

        match result with
        | Ok _ -> failwith "error"
        | Error err -> test <@ err.ToString() = "failed" @>
    }

[<Fact>]
let ``Simple error then ok is Error`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let req = error "failed" >=> unit 42

        // Act
        let! result = req |> runAsync ctx

        // Assert
        test <@ Result.isError result @>

        match result with
        | Ok _ -> failwith "error"
        | Error err -> test <@ err.ToString() = "failed" @>
    }

[<Fact>]
let ``Simple ok then error is Error`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let req = unit 42 >=> error "failed"

        // Act
        let! result = req |> runAsync ctx

        // Assert
        match result with
        | Ok _ -> failwith "error"
        | Error err -> test <@ err.ToString() = "failed" @>
    }

[<Fact>]
let ``Catching ok is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let errorHandler = badRequestHandler 420

        let req =
            unit 42
            >=> map (fun a -> a * 10)
            >=> catch errorHandler

        // Act
        let! content = req |> runUnsafeAsync ctx

        // Assert
        test <@ content = 420 @>
    }

[<Fact>]
let ``Catching errors is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let errorHandler = badRequestHandler 420

        let req = unit 42 >=> error "failed" >=> catch errorHandler

        // Act
        let! content = req |> runUnsafeAsync ctx

        // Assert
        test <@ content = 420 @>
    }

[<Fact>]
let ``Not catching errors is Error`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let errorHandler = badRequestHandler 420

        let req = unit 42 >=> catch errorHandler >=> error "failed"

        // Act
        let! result = req |> runAsync ctx

        // Assert
        match result with
        | Ok _ -> failwith "error"
        | Error err -> test <@ err.ToString() = "failed" @>
    }

[<Fact>]
let ``Sequential handlers is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let req = sequential [ unit 1; unit 2; unit 3; unit 4; unit 5 ]

        // Act
        let! content = req |> runUnsafeAsync ctx

        // Assert
        test <@ content = [ 1; 2; 3; 4; 5 ] @>
    }

[<Fact>]
let ``Sequential handlers with an Error is Error`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let req = sequential [ unit 1; unit 2; error "fail"; unit 4; unit 5 ]

        // Act
        let! result = req |> runAsync ctx

        // Assert
        test <@ Result.isError result @>

        match result with
        | Ok _ -> failwith "expected failure"
        | Error err -> test <@ err.ToString() = "fail" @>
    }

[<Fact>]
let ``Concurrent handlers is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let req = concurrent [ unit 1; unit 2; unit 3; unit 4; unit 5 ]

        // Act
        let! result = req |> runAsync ctx

        // Assert
        test <@ Result.isOk result @>

        match result with
        | Ok content -> test <@ content = Some [ 1; 2; 3; 4; 5 ] @>
        | Error err -> failwith "error"
    }

[<Fact>]
let ``Concurrent handlers with an Error is Error`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext
        let req = concurrent [ unit 1; unit 2; error "fail"; unit 4; unit 5 ]

        // Act
        let! result = req |> runAsync ctx

        // Assert
        test <@ Result.isError result @>

        match result with
        | Ok _ -> failwith "expected failure"
        | Error err -> test <@ err.ToString() = "fail" @>
    }

[<Property>]
let ``Chunked handlers is Ok`` (PositiveInt chunkSize) (PositiveInt maxConcurrency) =
    task {
        // Arrange
        let ctx = Context.defaultContext

        let req =
            chunk<unit, int, int> chunkSize maxConcurrency unit [ 1; 2; 3; 4; 5 ]

        // Act
        let! result = req |> runUnsafeAsync ctx
        test <@ Seq.toList result = [ 1; 2; 3; 4; 5 ] @>
    }
    |> fun x -> x.Result

// [<Fact>]
// let ``Request with token renewer sets Authorization header`` () =
//     task {
//         // Arrange
//         let renewer _ = Ok "token" |> Task.FromResult
//         let ctx = Context.defaultContext

//         let req = withTokenRenewer renewer >=> unit 42

//         // Act
//         let! result = req |> runAsync' ctx

//         // Assert
//         match result with
//         | Ok ctx ->
//             let found =
//                 ctx.Request.Headers.TryGetValue "Authorization"
//                 |> (fun (found, value) -> found && value.Contains "token")

//             test <@ found @>
//         | Error err -> raise err
//     }

[<Fact>]
let ``Request with token renewer without token gives error`` () =
    task {
        // Arrange
        let err = Exception "Unable to authenticate"
        let renewer _ = err |> Error |> Task.FromResult
        let ctx = Context.defaultContext

        let req = withTokenRenewer renewer >=> unit 42

        // Act
        let! result = req |> runAsync ctx
        // Assert
        match result with
        | Ok ctx -> failwith "Request should fail"
        | Error err -> test <@ err.ToString().Contains("Unable to authenticate") @>
    }

[<Fact>]
let ``Request with token renewer throws exception gives error`` () =
    task {
        // Arrange
        let err = Exception "Unable to authenticate"
        let renewer _ = failwith "failing" |> Task.FromResult
        let ctx = Context.defaultContext

        let req = withTokenRenewer renewer >=> unit 42

        // Act
        let! result = req |> runAsync ctx
        // Assert
        match result with
        | Ok ctx -> failwith "Request should fail"
        | Error err -> test <@ err.ToString().Contains("failing") @>
    }
