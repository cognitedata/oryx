namespace Oryx.SystemTextJson

open System.Net.Http
open System.IO
open System.Net
open System.Net.Http.Headers
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

open System.Text.Json

/// HttpContent implementation to push content directly to the output stream.
type JsonPushStreamContent<'T> (content : 'T, options : JsonSerializerOptions) =
    inherit HttpContent ()
    let _content = content
    let _options = options
    do
        base.Headers.ContentType <- MediaTypeHeaderValue "application/json"

    new (content : 'T) =
        let options = JsonSerializerOptions()
        new JsonPushStreamContent<'T>(content, options)

    override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
        task {
            do! JsonSerializer.SerializeAsync<'T>(stream, _content, _options)
            do! stream.FlushAsync()
        } :> _

    override this.TryComputeLength(length: byref<int64>) : bool =
        length <- -1L
        false

    override this.ToString() =
        _content.ToString()