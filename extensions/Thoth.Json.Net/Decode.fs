// Copyright 2019 Cognite AS

namespace Oryx.Thoth

open System
open System.IO

open Thoth.Json.Net
open FSharp.Control.Tasks.V2.ContextInsensitive
open Newtonsoft.Json

[<AutoOpen>]
module Decode =
    let decodeStreamAsync (decoder : Decoder<'a>) (stream : IO.Stream) =
        task {
            use tr = new StreamReader(stream) // StreamReader will dispose the stream
            use jtr = new JsonTextReader(tr, DateParseHandling = DateParseHandling.None)
            let! json = Newtonsoft.Json.Linq.JValue.ReadFromAsync jtr

            return Decode.fromValue "$" decoder json
        }
