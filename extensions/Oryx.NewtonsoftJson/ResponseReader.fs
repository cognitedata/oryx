namespace Oryx.NewtonsoftJson

open System.IO

open FSharp.Control.Tasks.V2.ContextInsensitive
open Newtonsoft.Json
open Oryx

module ResponseReader =
    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <param name="next">The next handler to use.</param>
    /// <param name="context">HttpContext.</param>
    /// <returns>Decoded context.</returns>
    let json<'a, 'r, 'err> (next: NextFunc<'a,'r, 'err>) (context: HttpContext) : HttpFuncResult<'r, 'err> =
        task {
            let! stream = context.Response.Content.ReadAsStreamAsync ()
            use sr = new StreamReader(stream)
            let serializer = JsonSerializer()
            use jtr = new JsonTextReader(sr, DateParseHandling = DateParseHandling.None)

            let ret =
                try
                    serializer.Deserialize<'a> (jtr) |> Ok
                with
                | ex ->  Error (ex.ToString ())

            match ret with
            | Ok result ->
                return! next { Request = context.Request; Response = result }
            | Error error ->
                return Error (Panic <| JsonDecodeException error)
        }

