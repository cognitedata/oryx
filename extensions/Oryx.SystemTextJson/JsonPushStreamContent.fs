namespace Oryx.SystemTextJson

open System.Net.Http
open System.IO
open System.Net
open System.Net.Http.Headers
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

open System.Text.Json

/// HttpContent implementation to push content directly to the output stream.
type JsonPushStreamContent<'a> (content : 'a, options : JsonSerializerOptions) =
    inherit HttpContent ()
    let _content = content
    let _options = options
    do
        base.Headers.ContentType <- MediaTypeHeaderValue "application/json"

    new (content : 'a) =
        let options = JsonSerializerOptions()
        new JsonPushStreamContent<'a>(content, options)

    override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
        task {
            do! JsonSerializer.SerializeAsync<'a>(stream, _content, _options)
            do! stream.FlushAsync()
        } :> _

    override this.TryComputeLength(length: byref<int64>) : bool =
        length <- -1L
        false

    override this.ToString() =
        _content.ToString()