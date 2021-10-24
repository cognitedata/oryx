module Tests.Handler

open System
open System.Threading.Tasks

open FSharp.Control.Tasks
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
        let ctx = HttpContext.defaultContext

        // Act
        let! content =
            singleton 43
            |> runUnsafeAsync

        // Assert
        test <@ content = 43 @>
    }

[<Fact>]
let ``Simple error handler is Error`` () =
    task {
        // Arrange
        let ctx = empty

        // Act
        let! result = empty |> error "failed" |> runAsync

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
        let ctx = empty
        let req = error "failed" >> singleton 42

        // Act
        let! result = req |> runAsync

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
        let req = singleton 42 |> error "failed"

        // Act
        let! result = req |> runAsync

        // Assert
        match result with
        | Ok _ -> failwith "error"
        | Error err -> test <@ err.ToString() = "failed" @>
    }

[<Fact>]
let ``Catching ok is Ok`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext
        let errorHandler = badRequestHandler 420

        let req =
            singleton 42
            |> map (fun a -> a * 10)
            |> catch errorHandler

        // Act
        let! content = req |> runUnsafeAsync ctx

        // Assert
        test <@ content = 420 @>
    }

[<Fact>]
let ``Catching errors is Ok`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext
        let errorHandler = badRequestHandler 420

        let req =
            singleton 42
            >=> error "failed"
            >=> catch errorHandler

        // Act
        let! content = req |> runUnsafeAsync ctx

        // Assert
        test <@ content = 420 @>
    }

[<Fact>]
let ``Catching panic is not possible`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext
        let errorHandler = badRequestHandler 420

        let req =
            singleton 42
            >=> panic "panic!"
            >=> catch errorHandler

        // Act
        let! result = req |> runAsync ctx

        // Assert
        match result with
        | Ok _ -> failwith "should panic!"
        | Error err -> test <@ err.ToString() = "panic!" @>
    }

[<Fact>]
let ``Not catching errors is Error`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext
        let errorHandler = badRequestHandler 420

        let req =
            singleton 42
            >=> catch errorHandler
            >=> error "failed"

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
        let ctx = HttpContext.defaultContext

        let req =
            sequential [ singleton 1; singleton 2; singleton 3; singleton 4; singleton 5 ]

        // Act
        let! content = req |> runUnsafeAsync ctx

        // Assert
        test <@ content = [ 1; 2; 3; 4; 5 ] @>
    }

[<Fact>]
let ``Sequential handlers with an Error is Error`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        let req =
            sequential [ singleton 1; singleton 2; error "fail"; singleton 4; singleton 5 ]

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
        let ctx = HttpContext.defaultContext

        let req =
            concurrent [ singleton 1; singleton 2; singleton 3; singleton 4; singleton 5 ]

        // Act
        let! result = req |> runAsync ctx

        // Assert
        test <@ Result.isOk result @>

        match result with
        | Ok content -> test <@ content = [ 1; 2; 3; 4; 5 ] @>
        | Error err -> failwith "error"
    }

[<Fact>]
let ``Concurrent handlers with an Error is Error`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        let req =
            concurrent [ singleton 1; singleton 2; error "fail"; singleton 4; singleton 5 ]

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
        let ctx = HttpContext.defaultContext

        let req =
            chunk<unit, int, int> chunkSize maxConcurrency singleton [ 1; 2; 3; 4; 5 ]

        // Act
        let! result = req |> runUnsafeAsync ctx
        test <@ Seq.toList result = [ 1; 2; 3; 4; 5 ] @>
    }
    |> fun x -> x.Result

[<Fact>]
let ``Choose handlers is Ok`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        let req = choose [ error "1"; singleton 2; error "3"; singleton 4 ]

        // Act
        let! result = req |> runUnsafeAsync ctx
        test <@ result = 2 @>
    }

[<Fact>]
let ``Choose panic is Error`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        let req = choose [ error "1"; panic "2"; error "3"; singleton 4 ]

        // Act
        try
            let! result = req |> runUnsafeAsync ctx
            assert false
        with
        | PanicException (_) -> ()
        | _ -> failwith "Should be panic"
    }

[<Fact>]
let ``Choose panic is not skipped`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        let req =
            choose [ error "1"; choose [ panic "2"; singleton 42; error "3" ]; singleton 4 ]

        // Act
        try
            let! result = req |> runUnsafeAsync ctx
            assert false
        with
        | PanicException (_) -> ()
        | _ -> failwith "Should be panic"
    }

[<Fact>]
let ``Choose empty is SkipException`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        let req = choose []

        // Act
        try
            let! result = req |> runUnsafeAsync ctx
            assert false
        with
        | SkipException (_) -> ()
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
        let ctx = HttpContext.defaultContext

        let req = withTokenRenewer renewer >=> singleton 42

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
        let ctx = HttpContext.defaultContext

        let req = withTokenRenewer renewer >=> singleton 42

        // Act
        let! result = req |> runAsync ctx
        // Assert
        match result with
        | Ok ctx -> failwith "Request should fail"
        | Error err -> test <@ err.ToString().Contains("failing") @>
    }
