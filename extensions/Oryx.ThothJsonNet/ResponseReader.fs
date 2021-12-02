namespace Oryx.ThothJsonNet

open System.IO
open System.Net.Http
open System.Threading.Tasks

open FSharp.Control.Tasks
open Thoth.Json.Net

open Oryx

exception JsonDecodeException of string

module ResponseReader =

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use.</param>
    /// <param name="source">The upstream source handler.</param>
    /// <returns>Decoded context.</returns>
    let json<'TResult> (decoder: Decoder<'TResult>) (source: HttpHandler<HttpContent>) : HttpHandler<'TResult> =
        let parser (stream: Stream) : ValueTask<'TResult> =
            vtask {
                let! ret = decodeStreamAsync decoder stream

                match ret with
                | Ok result -> return result
                | Error error -> return raise (JsonDecodeException error)
            }

        parseAsync parser source
