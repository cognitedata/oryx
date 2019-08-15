namespace Oryx

open System

open Thoth.Json.Net

type ErrorValue () =
    static member val Decoder : Decoder<ErrorValue> =
        Decode.oneOf [
            Decode.int |> Decode.map (fun value -> IntegerValue value :> ErrorValue)
            Decode.float |> Decode.map (fun value -> FloatValue value :> ErrorValue)
            Decode.string |> Decode.map (fun value ->StringValue value :> ErrorValue)
        ]

and IntegerValue (value: int) =
    inherit ErrorValue ()
    member val Integer = value with get, set

    override this.ToString () =
        sprintf "%d" this.Integer

and FloatValue (value) =
    inherit ErrorValue ()

    member val Float = value with get, set
    override this.ToString () =
        sprintf "%f" this.Float

and StringValue (value) =
    inherit ErrorValue ()
    member val String = value with get, set
    override this.ToString () =
        sprintf "%s" this.String

/// Models the error response from REST APIs.
type ResponseError = {
    Code : int
    Message : string
    Missing : Map<string, ErrorValue> seq
    Duplicated : Map<string, ErrorValue> seq
    InnerException : exn option
}
    with
        static member Decoder : Decoder<ResponseError> =
            Decode.object (fun get ->
                let message = get.Required.Field "message" Decode.string
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

        static member empty =
            {
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

