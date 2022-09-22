// Copyright 2019 Cognite AS

namespace Oryx.ThothJsonNet

open System

open Newtonsoft.Json.Linq
open Thoth.Json.Net


[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Encode =
    let inline stringify encoder = Encode.toString 0 encoder

    /// Encode int64 to Json number (not to string as Thoth.Json.Net)
    let inline int53 (value: int64) : JsonValue = JValue(value) :> JsonValue

    /// Encode int64 seq to Json number array.
    let inline int53seq (items: seq<int64>) = Seq.map int53 items |> Encode.seq

    /// Encode int64 list to Json number array.
    let inline int53list (items: int64 list) = List.map int53 items |> Encode.list

    /// Encode int64 list to string for use in URL query strings
    let int53listStringify = int53list >> stringify

    /// Encode URI.
    let inline uri (value: Uri) : JsonValue = Encode.string (value.ToString())

    /// Encode a string/string Map.
    let inline propertyBag (values: Map<string, string>) =
        values |> Map.map (fun _ value -> Encode.string value) |> Encode.dict

    /// Encode optional property.
    let optionalProperty<'T> (name: string) (encoder: 'T -> JsonValue) (value: 'T option) =
        [ if value.IsSome then
              name, encoder value.Value ]
