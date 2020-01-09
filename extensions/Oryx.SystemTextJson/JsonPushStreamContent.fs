namespace Oryx.SystemTextJson

open System.Net.Http
open System.IO
open System.Net
open System.Net.Http.Headers
open System.Threading.Tasks

open FSharp.Control.Tasks.V2.ContextInsensitive

open System.Text.Json

/// HttpContent implementation to push a JsonValue directly to the output stream.
type JsonPushStreamContent<'a> (content : 'a) =
    inherit HttpContent ()
    let _content = content
    do
        base.Headers.ContentType <- MediaTypeHeaderValue "application/json"

    override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
        task {
            do! JsonSerializer.SerializeAsync<'a>(stream, _content)
            do! stream.FlushAsync()
        } :> _
    override this.TryComputeLength(length: byref<int64>) : bool =
        length <- -1L
        false
