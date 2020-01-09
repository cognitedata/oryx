namespace Oryx.Thoth

open FSharp.Control.Tasks.V2.ContextInsensitive
open Oryx
open Thoth.Json.Net

[<AutoOpen>]
module ResponseReader =

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <param name="next">The next handler to use.</param>
    /// <param name="context">HttpContext.</param>
    /// <returns>Decoded context.</returns>
    let json<'a, 'r, 'err> (decoder : Decoder<'a>) (next: NextFunc<'a,'r, 'err>) (context: HttpContext) : HttpFuncResult<'r, 'err> =
        task {
            let response = context.Response
            let! stream = response.Content.ReadAsStreamAsync ()
            let! ret = decodeStreamAsync decoder stream
            match ret with
            | Ok result ->
                return! next { Request = context.Request; Response = result }
            | Error error ->
                return Error (Panic <| JsonDecodeException error)
        }

