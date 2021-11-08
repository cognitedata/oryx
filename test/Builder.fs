module Tests.Builder

open FSharp.Control.Tasks
open Swensen.Unquote
open Xunit

open Oryx
open Tests.Common


[<Fact>]
let ``Zero builder is Ok`` () =
    task {
        // Arrange
        let req = http { () }

        // Act
        let! result = req |> runAsync

        // Assert
        test <@ Result.isOk result @>
    }

[<Fact>]
let ``Simple unit handler in builder is Ok`` () =
    task {
        // Arrange
        // Act
        let! result =
            let a =
                http {
                    let! value = singleton 42
                    return value
                }

            a |> runUnsafeAsync

        // Assert
        test <@ result = 42 @>
    }

[<Fact>]
let ``Simple return from unit handler in builder is Ok`` () =
    task {
        // Arrange
        let _ = singleton 42 |> ignoreContent |> runAsync

        // Act
        let! result = http { return! singleton 42 } |> runUnsafeAsync

        // Assert
        test <@ result = 42 @>
    }

[<Fact>]
let ``Multiple handlers in builder is Ok`` () =
    task {
        // Arrange
        let request =
            http {
                let! a = singleton 10
                let! b = singleton 20

                return! add a b |> validate (fun value -> value = 10 + 20)
            }

        // Act
        let! result = request |> runUnsafeAsync

        // Assert
        test <@ result = 30 @>
    }

[<Fact>]
let ``Get value 2 is Ok`` () =
    task {
        // Arrange
        let request =
            http { yield 42 }
            |> HttpHandler.bind (fun a -> http { return a + 1 })

        // Act
        let! result = request |> runUnsafeAsync

        // Assert
        test <@ result = 43 @>
    }

[<Fact>]
let ``Iterate handlers is Ok`` () =
    task {
        // Arrange
        let request =
            http {
                let! h =
                    http {
                        for _ in 1 .. 10 do
                            yield 42
                    }

                return List.sum h
            }

        // Act
        let! result = request |> runUnsafeAsync

        // Assert
        test <@ result = 420 @>
    }
