namespace Oryx.NewtonsoftJson

open System.IO
open System.Net.Http

open Newtonsoft.Json
open Oryx

module ResponseReader =
    /// <summary>
    /// JSON decode response and map decode error string to exception so we don't get more response error types.
    /// </summary>
    /// <param name="decoder">Decoder to use. </param>
    /// <returns>Decoded context.</returns>
    let json<'TResult> : HttpHandler<HttpContent, 'TResult> =

        let parser (stream: Stream) =
            use sr = new StreamReader(stream)
            let serializer = JsonSerializer()

            use jtr =
                new JsonTextReader(sr, DateParseHandling = DateParseHandling.None)

            serializer.Deserialize<'TResult>(jtr)

        parse parser
