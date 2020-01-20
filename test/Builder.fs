module Tests.Builder

open FSharp.Control.Tasks.V2
open Swensen.Unquote
open Xunit

open Oryx
open Tests.Common


[<Fact>]
let ``Zero builder is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext

    // Act
    let! result = req { () } |> runAsync ctx

    // Assert
    test <@ Result.isOk result @>
}

[<Fact>]
let ``Simple unit handler in builder is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext

    // Act
    let! result =
        req {
            let! value = unit 42
            return value
        }
        |> runAsync ctx

    // Assert
    test <@ Result.isOk result @>
    match result with
    | Ok value -> test <@ value = 42 @>
    | Error (Panic err) -> raise err
    | Error (ResponseError err) -> failwith (err.ToString())
}

[<Fact>]
let ``Simple return from unit handler in builder is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext

    let a = unit 42 finishEarly

    // Act
    let! result =
        req { return! unit 42 }
        |> runAsync ctx

    // Assert
    test <@ Result.isOk result @>
    match result with
    | Ok value -> test <@ value = 42 @>
    | Error (Panic err) -> raise err
    | Error (ResponseError err) -> failwith (err.ToString())
}

[<Fact>]
let ``Multiple handlers in builder is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext

    // Act
    let request = req {
        let! a = unit 10
        let! b = unit 20
        return! add a b
    }

    let! result = request |> runAsync ctx

    // Assert
    test <@ Result.isOk result @>
    match result with
    | Ok value -> test <@ value = 30 @>
    | Error (Panic err) -> raise err
    | Error (ResponseError err) -> failwith (err.ToString())
}
