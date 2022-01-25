// Copyright 2019 Cognite AS

namespace Oryx.ThothJsonNet

open System
open System.IO

open Thoth.Json.Net
open FSharp.Control.TaskBuilder
open Newtonsoft.Json

[<AutoOpen>]
module Decode =
    let decodeStreamAsync (decoder: Decoder<'T>) (stream: IO.Stream) =
        task {
            use tr = new StreamReader(stream) // StreamReader will dispose the stream

            use jtr =
                new JsonTextReader(tr, DateParseHandling = DateParseHandling.None)

            let! json = Newtonsoft.Json.Linq.JValue.ReadFromAsync jtr

            return Decode.fromValue "$" decoder json
        }
