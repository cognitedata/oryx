namespace Oryx.ThothJsonNet

open System.IO
open System.Net.Http
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive
open Thoth.Json.Net

open Oryx

exception JsonDecodeException of string

module ResponseReader =

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <returns>Decoded context.</returns>
    let json<'T, 'TResult, 'TError> (decoder: Decoder<'T>): HttpHandler<HttpContent, 'T, 'TResult, 'TError> =
        let parser (stream: Stream): Task<'T> =
            task {
                let! ret = decodeStreamAsync decoder stream

                match ret with
                | Ok result -> return result
                | Error error -> return raise (JsonDecodeException error)
            }

        parseAsync parser
