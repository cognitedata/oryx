module Tests.Handler

open System
open System.Threading.Tasks

open FSharp.Control.TaskBuilder
open Swensen.Unquote
open Xunit
open FsCheck.Xunit
open FsCheck

open Oryx
open Tests.Common

[<Fact>]
let ``Simple unit handler is Ok`` () =
    task {
        // Arrange
        let req = singleton 43

        // Act
        let! content = req |> runUnsafeAsync

        // Assert
        test <@ content = 43 @>
    }

[<Fact>]
let ``Simple error handler is Error`` () =
    task {
        // Arrange
        let req = httpRequest |> error "failed"

        // Act
        let! result = req |> runAsync

        // Assert
        test <@ Result.isError result @>

        match result with
        | Ok _ -> failwith "error"
        | Error err -> test <@ err.ToString() = "failed" @>
    }

[<Fact>]
let ``Catching ok is Ok`` () =
    task {
        // Arrange
        let errorHandler = badRequestHandler 420

        let req =
            singleton 42
            |> map (fun a -> a * 10)
            |> catch errorHandler

        // Act
        let! content = req |> runUnsafeAsync

        // Assert
        test <@ content = 420 @>
    }

[<Fact>]
let ``Catching errors is Ok`` () =
    task {
        // Arrange
        let errorHandler = badRequestHandler 420

        let req =
            singleton 42
            |> error "failed"
            |> catch errorHandler

        // Act
        let! content = req |> runUnsafeAsync

        // Assert
        test <@ content = 420 @>
    }

[<Fact>]
let ``Catching panic is not possible`` () =
    task {
        // Arrange
        let errorHandler = badRequestHandler 420

        let req =
            singleton 42
            |> catch errorHandler
            |> panic "panic!"

        // Act
        let! result = req |> runAsync

        // Assert
        match result with
        | Ok _ -> failwith "should panic!"
        | Error err -> test <@ err.ToString() = "panic!" @>
    }

[<Fact>]
let ``Not catching errors is Error`` () =
    task {
        // Arrange
        let errorHandler = badRequestHandler 420

        let handleError source =
            source
            |> error "first!"
            |> catch errorHandler
            |> error "second!"

        let req = singleton 42 |> handleError

        // Act
        let! result = req |> runAsync

        // Assert
        match result with
        | Ok _ -> failwith "error"
        | Error err -> test <@ err.ToString() = "second!" @>
    }

[<Fact>]
let ``Sequential handlers is Ok`` () =
    task {
        // Arrange
        let req =
            sequential [ singleton 1
                         singleton 2
                         singleton 3
                         singleton 4
                         singleton 5 ]

        // Act
        let! content = req |> runUnsafeAsync

        // Assert
        test <@ content = [ 1; 2; 3; 4; 5 ] @>
    }

[<Fact>]
let ``Sequential handlers with an Error is Error`` () =
    task {
        // Arrange
        let req =
            sequential [ singleton 1
                         singleton 2
                         ofError "fail"
                         singleton 4
                         singleton 5 ]

        // Act
        let! result = req |> runAsync

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
        let req =
            concurrent [ singleton 1
                         singleton 2
                         singleton 3
                         singleton 4
                         singleton 5 ]

        // Act
        let! result = req |> runAsync

        // Assert
        test <@ Result.isOk result @>

        match result with
        | Ok content -> test <@ content = [ 1; 2; 3; 4; 5 ] @>
        | _ -> failwith "error"
    }

[<Fact>]
let ``Concurrent handlers with an Error is Error`` () =
    task {
        // Arrange
        let req =
            concurrent [ singleton 1
                         singleton 2
                         ofError "fail"
                         singleton 4
                         singleton 5 ]

        // Act
        let! result = req |> runAsync

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
        let req = chunk<unit, int, int> chunkSize maxConcurrency singleton [ 1; 2; 3; 4; 5 ]

        // Act
        let! result = req |> runUnsafeAsync
        test <@ Seq.toList result = [ 1; 2; 3; 4; 5 ] @>
    }
    |> fun x -> x.Result

[<Fact>]
let ``Choose handlers is Ok`` () =
    task {
        // Arrange
        let req =
            httpRequest
            |> choose [ error "1"
                        replace 2
                        error "3"
                        replace 4 ]

        // Act
        let! result = req |> runUnsafeAsync
        test <@ result = 2 @>
    }

[<Fact>]
let ``Choose panic is Error`` () =
    task {
        // Arrange
        let req: HttpHandler<int> =
            httpRequest
            |> choose [ error "1"
                        panic "2"
                        error "3"
                        replace 4 ]

        // Act
        try
            let! _ = req |> runUnsafeAsync
            assert false
        with
        | :? PanicException -> ()
        | _ -> failwith "Should be panic"
    }

[<Fact>]
let ``Choose panic is not skipped`` () =
    task {
        // Arrange
        let req =
            httpRequest
            |> choose [ error "1"
                        choose [ panic "2"
                                 replace 42
                                 error "3" ]
                        replace 4 ]

        // Act
        try
            let! _ = req |> runUnsafeAsync
            assert false
        with
        | :? PanicException -> ()
        | _ -> failwith "Should be panic"
    }

[<Fact>]
let ``Choose empty is SkipException`` () =
    task {
        // Arrange
        let req = httpRequest |> choose []

        // Act
        try
            let! _ = req |> runUnsafeAsync
            assert false
        with
        | :? SkipException -> ()
        | _ -> failwith "Should be skip"

    }

// [<Fact>]
// let ``Request with token renewer sets Authorization header`` () =
//     task {
//         // Arrange
//         let renewer _ = Ok "token" |> Task.FromResult
//         let ctx = HttpContext.defaultContext

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

        let req = singleton 42 |> withTokenRenewer renewer

        // Act
        let! result = req |> runAsync
        // Assert
        match result with
        | Ok _ -> failwith "Request should fail"
        | Error err -> test <@ err.ToString().Contains("Unable to authenticate") @>
    }

[<Fact>]
let ``Request with token renewer throws exception gives error`` () =
    task {
        // Arrange
        let renewer _ = failwith "failing" |> Task.FromResult
        let req = singleton 42 |> withTokenRenewer renewer

        // Act
        let! result = req |> runAsync

        // Assert
        match result with
        | Ok _ -> failwith "Request should fail"
        | Error err -> test <@ err.ToString().Contains("failing") @>
    }
