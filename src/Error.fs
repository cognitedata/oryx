// Copyright 2019 Cognite AS

namespace Oryx

open System
open Thoth.Json.Net

type ErrorValue =
    | IntegerValue of int
    | FloatValue of float
    | StringValue of string

    with
        static member Decoder : Decoder<ErrorValue> =
            Decode.oneOf [
                Decode.int |> Decode.map IntegerValue
                Decode.float |> Decode.map FloatValue
                Decode.string |> Decode.map StringValue
            ]

type ResponseError = {
    Code : int
    Message : string
    Missing : Map<string, ErrorValue> seq
    Duplicated : Map<string, ErrorValue> seq
    InnerException : exn option } with
        static member Decoder : Decoder<ResponseError> =
            Decode.object (fun get ->
                {
                    Code = get.Required.Field "code" Decode.int
                    Message = get.Required.Field "message" Decode.string
                    Missing =
                        let missing = get.Optional.Field "missing" (Decode.array (Decode.dict ErrorValue.Decoder))
                        match missing with
                        | Some missing -> missing |> Seq.ofArray
                        | None -> Seq.empty
                    Duplicated =
                        let duplicated = get.Optional.Field "duplicated" (Decode.array (Decode.dict ErrorValue.Decoder))
                        match duplicated with
                        | Some duplicated -> duplicated |> Seq.ofArray
                        | None -> Seq.empty
                    InnerException = None
                })

        static member empty = {
           Code = 400
           Message = String.Empty
           Missing = Seq.empty
           Duplicated = Seq.empty
           InnerException = None
       }

type ApiResponseError = {
    Error : ResponseError
} with
    static member Decoder =
        Decode.object (fun get ->
            {
                Error = get.Required.Field "error" ResponseError.Decoder
            }
        )
