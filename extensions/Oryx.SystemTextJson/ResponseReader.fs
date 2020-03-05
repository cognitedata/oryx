namespace Oryx.SystemTextJson

open System.Net.Http
open System.Text.Json

open Oryx

module ResponseReader =

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="options">JSON serializer options to use. </param>
    /// <returns>Decoded context.</returns>
    let json<'T, 'TResult, 'TError> (options: JsonSerializerOptions) : HttpHandler<HttpResponseMessage, 'T, 'TResult, 'TError> =
        let parser stream = (JsonSerializer.DeserializeAsync<'a>(stream, options)).AsTask()
        parseAsync parser
