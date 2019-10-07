module Tests.Handler

open Xunit
open Swensen.Unquote

open System.Threading.Tasks

open FSharp.Control.Tasks.V2

open Tests.Common

open Oryx

let unit (value: 'a) (next: NextFunc<'a, 'b>) (context: HttpContext) : Task<Context<'b>> =
    next { Request=context.Request; Result = Ok value }

let add (a: int) (b: int) (next: NextFunc<int, 'b>) (context: HttpContext) : Task<Context<'b>> =
    unit (a + b) next context

let error msg (next: NextFunc<'a, 'b>) (context: Context<'a>) : Task<Context<'b>> =
    Task.FromResult { Request=context.Request; Result = { ResponseError.empty with Message=msg } |> Error }

[<Fact>]
let ``Simple unit handler is Ok``() = task {
    // Arrange
    let ctx = Context.defaultContext

    // Act
    let! ctx' = unit 42 Task.FromResult ctx
    let result = ctx'.Result

    // Assert
    test <@ Result.isOk result @>
    match result with
    | Ok value -> test <@ value = 42 @>
    | Error err -> failwith err.Message
}

[<Fact>]
let ``Simple error handler is Error``() = task {
    // Arrange
    let ctx = Context.defaultContext

    // Act
    let! ctx' = error "failed" Task.FromResult ctx
    let result = ctx'.Result

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
    let! ctx' = req Task.FromResult ctx
    let result = ctx'.Result

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
    let! ctx' = req Task.FromResult ctx
    let result = ctx'.Result

    // Assert
    test <@ Result.isError result @>
    match result with
    | Ok _ -> failwith "error"
    | Error err -> test <@ err.Message = "failed" @>
}
