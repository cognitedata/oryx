namespace Oryx

open System
open System.IO

open Newtonsoft.Json.Linq
open Thoth.Json.Net


[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Encode =
    let inline stringify encoder = Encode.toString 0 encoder

    /// Encode int64 to Json number (not to string as Thoth.Json.Net)
    let inline int53 (value : int64) : JsonValue = JValue(value) :> JsonValue

    /// Encode int64 seq to Json number array.
    let inline int53Seq (items: int64 seq) = Seq.map int53 items |> Encode.seq

    /// Encode int64 list to Json number array.
    let inline int53List (items: int64 list) = List.map int53 items |> Encode.list

    /// Encode int64 list to string for use in URL query strings
    let inline int53ListStringify (values: int64 list) = int53List >> stringify

    /// Encode URI.
    let inline uri (value: Uri) : JsonValue =
        Encode.string (value.ToString ())

    /// Encode a string/string Map.
    let inline propertyBag (values: Map<string, string>) =  values |> Map.map (fun _ value -> Encode.string value) |> Encode.dict

    /// Encode optional property.
    let optionalProperty<'a> (name: string) (encoder: 'a -> JsonValue) (value : 'a option) =
        [
            if value.IsSome then
                yield name, encoder value.Value
        ]

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <param name="resultMapper">Mapper for transforming the result.</param>
    /// <param name="next">The next async handler to use.</param>
    /// <returns>Decoded context.</returns>
    let decodeResponse<'a, 'b, 'c> (decoder : Decoder<'a>) (resultMapper : 'a -> 'b) (next: NextHandler<'b,'c>) (context: Context<Stream>) =
        async {
            let result = context.Result

            let! nextResult = async {
                match result with
                | Ok stream ->
                    let! ret = decodeStreamAsync decoder stream |> Async.AwaitTask
                    match ret with
                    | Ok value -> return value |> resultMapper |> Ok
                    | Error message ->
                        return {
                            ResponseError.empty with
                                Message = message
                        } |> Error
                | Error err -> return Error err
            }

            return! next { Request = context.Request; Result = nextResult }
        }
    let decodeProtobuf<'b, 'c> (parser : Stream -> 'b) (next: NextHandler<'b, 'c>) (context : Context<Stream>) =
        async {
            let result = context.Result |> Result.map parser
            return! next { Request = context.Request; Result = result }
        }
