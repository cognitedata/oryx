// Copyright 2019 Cognite AS

namespace Oryx

open System
open System.IO
open System.Threading.Tasks

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
            if value.IsSome then name, encoder value.Value
        ]

[<AutoOpen>]
module Decoders =
    let decodeStreamAsync (decoder : Decoder<'a>) (stream : IO.Stream) =
        task {
            use tr = new StreamReader(stream) // StreamReader will dispose the stream
            use jtr = new JsonTextReader(tr, DateParseHandling = DateParseHandling.None)
            let! json = Newtonsoft.Json.Linq.JValue.ReadFromAsync jtr

            return Decode.fromValue "$" decoder json
        }

module ResponseReaders =

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <param name="resultMapper">Mapper for transforming the result.</param>
    /// <param name="next">The next handler to use.</param>
    /// <returns>Decoded context.</returns>
    let json<'a, 'r, 'err> (decoder : Decoder<'a>) (next: NextFunc<'a,'r, 'err>) (context: HttpContext) : HttpFuncResult<'r, 'err> =
        task {
            let response = context.Response
            use! stream = response.Content.ReadAsStreamAsync ()
            let! ret = decodeStreamAsync decoder stream
            match ret with
            | Ok result ->
                return! next { Request = context.Request; Response = result }
            | Error error ->
                return Error (Panic <| JsonDecodeException error)
        }

    let protobuf<'b, 'r, 'err> (parser : Stream -> 'b) (next: NextFunc<'b, 'r, 'err>) (context : Context<HttpResponseMessage>) : Task<Result<Context<'r>,HandlerError<'err>>> =
        task {
            let response = context.Response
            use! stream = response.Content.ReadAsStreamAsync ()
            try
                let b = parser stream
                return! next { Request = context.Request; Response = b }

            with
            | ex -> return Error (Panic ex)
        }
