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
        let ctx = HttpContext.defaultContext

        // Act
        let! result = req { () } |> runAsync ctx

        // Assert
        test <@ Result.isOk result @>
    }

[<Fact>]
let ``Simple unit handler in builder is Ok`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

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
        let ctx = HttpContext.defaultContext

        let a = singleton 42 >=> forget |> runAsync ctx

        // Act
        let! result = req { return! singleton 42 } |> runUnsafeAsync ctx

        // Assert
        test <@ result = 42 @>
    }

[<Fact>]
let ``Multiple handlers in builder is Ok`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        // Act
        let request =
            req {
                let! a = singleton 10
                let! b = singleton 20

                return!
                    add a b
                    >=> validate (fun value -> value = 10 + 20)
            }

        let! result = request |> runUnsafeAsync ctx

        // Assert
        test <@ result = 30 @>
    }

[<Fact>]
let ``Get value is Ok`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        // Act
        let request =
            req {
                let! a =
                    HttpHandler.get ()
                    >=> validate (fun value -> value = 42)

                return a
            }

        let! result = singleton 42 >=> request |> runUnsafeAsync ctx

        // Assert
        test <@ result = 42 @>
    }

[<Fact>]
let ``Get value 2 is Ok`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        // Act
        let request =
            req {
                yield 42

                let! a = HttpHandler.get ()

                return a + 1
            }

        let! result = request |> runUnsafeAsync ctx

        // Assert
        test <@ result = 43 @>
    }

[<Fact>]
let ``Iterate handlers is Ok`` () =
    task {
        // Arrange
        let ctx = HttpContext.defaultContext

        // Act
        let request =
            req {
                let! h =
                    req {
                        for a in 1 .. 10 do
                            yield 42
                    }

                return List.sum h
            }

        let! result = request |> runUnsafeAsync ctx

        // Assert
        test <@ result = 420 @>
    }
