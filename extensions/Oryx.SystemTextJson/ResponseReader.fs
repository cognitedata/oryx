namespace Oryx.SystemTextJson

open System.Net.Http
open System.Text.Json

open Oryx

module ResponseReader =

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <param name="next">The next handler to use.</param>
    /// <param name="context">HttpContext.</param>
    /// <returns>Decoded context.</returns>
    let json<'a, 'r, 'err> (options: JsonSerializerOptions option) : HttpHandler<HttpResponseMessage, 'a, 'r, 'err> =
        let options =
            if options.IsSome
            then options.Value
            else JsonSerializerOptions(AllowTrailingCommas=true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

        let parser stream = (JsonSerializer.DeserializeAsync<'a>(stream, options)).AsTask()
        parseAsync parser
