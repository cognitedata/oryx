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
    let json<'T, 'TResult, 'TError> : HttpHandler<HttpResponseMessage, HttpResponse<'T>, 'TResult, 'TError> =

        let parser (stream: Stream): 'T =
            use sr = new StreamReader(stream)
            let serializer = JsonSerializer()

            use jtr =
                new JsonTextReader(sr, DateParseHandling = DateParseHandling.None)

            serializer.Deserialize<'T>(jtr)

        parse parser
