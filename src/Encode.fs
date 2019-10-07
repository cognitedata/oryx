// Copyright 2019 Cognite AS

namespace Oryx

open System
open System.IO

open Newtonsoft.Json.Linq
open Thoth.Json.Net
open System.Net.Http
open FSharp.Control.Tasks.ContextInsensitive
open Newtonsoft.Json


[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Encode =
    let inline stringify encoder = Encode.toString 0 encoder

    /// Encode int64 to Json number (not to string as Thoth.Json.Net)
    let inline int53 (value : int64) : JsonValue = JValue(value) :> JsonValue

    /// Encode int64 seq to Json number array.
    let inline int53seq (items: int64 seq) = Seq.map int53 items |> Encode.seq

    /// Encode int64 list to Json number array.
    let inline int53list (items: int64 list) = List.map int53 items |> Encode.list

    /// Encode int64 list to string for use in URL query strings
    let inline int53listStringify (values: int64 list) = int53list >> stringify

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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Decode =
    let decodeStreamAsync (decoder : Decoder<'a>) (stream : IO.Stream) =
        task {
            use tr = new StreamReader(stream) // StreamReader will dispose the stream
            use jtr = new JsonTextReader(tr, DateParseHandling = DateParseHandling.None)
            let! json = Newtonsoft.Json.Linq.JValue.ReadFromAsync jtr

            return Decode.fromValue "$" decoder json
        }

    let decodeError<'a> (next: NextFunc<HttpResponseMessage, 'a>) (context: Context<HttpResponseMessage>) =
        task {
            match context.Result with
            | Ok response ->
                if response.IsSuccessStatusCode then
                    return! next { Request = context.Request; Result = Ok response }
                else
                    use! stream = response.Content.ReadAsStreamAsync ()
                    let! result = decodeStreamAsync ApiResponseError.Decoder stream
                    match result with
                    | Ok result ->
                        return! next { Request = context.Request; Result = Error result.Error }
                    | Error error ->
                        return! next { Request = context.Request; Result = Error { ResponseError.empty with Message = error; Code = int response.StatusCode } }
            | Error error ->
                return { Request = context.Request; Result = Error error }
        }

    let decodeContent<'a, 'b, 'c> (decoder : Decoder<'a>) (resultMapper : 'a -> 'b) (next: NextFunc<'b,'c>) (context: Context<HttpResponseMessage>) =
        task {
            match context.Result with
            | Ok response ->
                use! stream = response.Content.ReadAsStreamAsync ()
                if response.IsSuccessStatusCode then
                    let! ret = decodeStreamAsync decoder stream
                    match ret with
                    | Ok result ->
                        return! next { Request = context.Request; Result = Ok (resultMapper result) }
                    | Error error ->
                        return { Request = context.Request; Result = Error { ResponseError.empty with Message = error }}
                else
                    return { Request = context.Request; Result = Error { ResponseError.empty with Message = "Error not decoded." }}
            | Error error ->
                return { Request = context.Request; Result = Error error }
        }

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <param name="resultMapper">Mapper for transforming the result.</param>
    /// <param name="next">The next handler to use.</param>
    /// <returns>Decoded context.</returns>
    let decodeResponse<'a, 'b, 'c> (decoder : Decoder<'a>) (resultMapper : 'a -> 'b) : HttpHandler<HttpResponseMessage, 'b, 'c> =
        decodeError
        >=> decodeContent decoder resultMapper

    let decodeProtobuf<'b, 'c> (parser : Stream -> 'b) (next: NextFunc<'b, 'c>) (context : Context<HttpResponseMessage>) =
        task {
            match context.Result with
            | Ok response ->
                use! stream = response.Content.ReadAsStreamAsync ()
                let result =
                    try
                        parser stream |> Ok
                    with
                    | ex -> Error { ResponseError.empty with InnerException=Some ex; Message="Unable to decode protobuf message." }
                return! next { Request = context.Request; Result = result }
            | Error error ->
                return { Request = context.Request; Result = Error error }
        }
