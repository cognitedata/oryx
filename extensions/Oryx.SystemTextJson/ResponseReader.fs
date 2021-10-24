namespace Oryx.SystemTextJson

open System.Net.Http
open System.Text.Json

open Oryx
open Oryx.Middleware

module ResponseReader =

    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="options">JSON serializer options to use. </param>
    /// <param name="source">The upstream source handler.</param>
    /// <returns>Decoded context.</returns>
    let json<'TResult> (options: JsonSerializerOptions) (source: IHttpHandler<HttpContent>) : IHttpHandler<'TResult> =
        let parser stream =
            (JsonSerializer.DeserializeAsync<'TResult>(stream, options))
                .AsTask()

        parseAsync parser source
