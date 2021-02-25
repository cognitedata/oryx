module Tests.Builder

open FSharp.Control.Tasks.V2
open Swensen.Unquote
open Xunit

open Oryx
open Tests.Common


[<Fact>]
let ``Zero builder is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext

        // Act
        let! result = req { () } |> runAsync ctx

        // Assert
        test <@ Result.isOk result @>
    }

[<Fact>]
let ``Simple unit handler in builder is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext

        // Act
        let! result =
            let a =
                req {
                    let! value = singleton 42
                    return value
                }

            a |> runUnsafeAsync ctx

        // Assert
        test <@ result = 42 @>
    }

[<Fact>]
let ``Simple return from unit handler in builder is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext

        let a = singleton 42 |> runAsync ctx

        // Act
        let! result = req { return! singleton 42 } |> runUnsafeAsync ctx

        // Assert
        test <@ result = 42 @>
    }

[<Fact>]
let ``Multiple handlers in builder is Ok`` () =
    task {
        // Arrange
        let ctx = Context.defaultContext

        // Act
        let request =
            req {
                let! a = singleton 10
                let! b = singleton 20
                return! add a b
            }

        let! result = request |> runUnsafeAsync ctx

        // Assert
        test <@ result = 30 @>
    }
