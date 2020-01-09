namespace Oryx.Thoth

open System.Text.Json
open System.IO
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

open Oryx

module ResponseReader =

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <param name="next">The next handler to use.</param>
    /// <param name="context">HttpContext.</param>
    /// <returns>Decoded context.</returns>
    let json<'a, 'r, 'err> (options: JsonSerializerOptions option) (next: NextFunc<'a,'r, 'err>) (context: HttpContext) : HttpFuncResult<'r, 'err> =
        let options =
            if options.IsSome
            then options.Value
            else JsonSerializerOptions(AllowTrailingCommas=true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

        task {
            use! stream = context.Response.Content.ReadAsStreamAsync ()
            try
                let! result = (JsonSerializer.DeserializeAsync<'a>(stream, options)).AsTask()
                return! next { Request = context.Request; Response = result }
            with
            | error -> return Error (Panic <| JsonDecodeException (error.ToString ()))
        }
