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
    let json<'T, 'TResult, 'TError> (next: HttpFunc<'T,'TResult, 'TError>) (context: HttpContext) : HttpFuncResult<'TResult, 'TError> =
        task {
            let! stream = context.Response.Content.ReadAsStreamAsync ()
            use sr = new StreamReader(stream)
            let serializer = JsonSerializer()
            use jtr = new JsonTextReader(sr, DateParseHandling = DateParseHandling.None)

            let ret =
                try
                    serializer.Deserialize<'T> (jtr) |> Ok
                with
                | ex ->  Error (Panic ex)

            match ret with
            | Ok result ->
                return! next { Request = context.Request; Response = result }
            | Error error ->
                return Error error
        }

