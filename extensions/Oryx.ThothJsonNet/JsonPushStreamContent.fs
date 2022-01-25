namespace Oryx.ThothJsonNet

open System.Net.Http
open System.IO
open System.Net
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks

open FSharp.Control.TaskBuilder

open Newtonsoft.Json
open Thoth.Json.Net

/// HttpContent implementation to push a JsonValue directly to the output stream.
type JsonPushStreamContent (content: JsonValue) =
    inherit HttpContent ()
    let _content = content
    do base.Headers.ContentType <- MediaTypeHeaderValue "application/json"

    override this.SerializeToStreamAsync(stream: Stream, context: TransportContext) : Task =
        task {
            use sw =
                new StreamWriter(stream, UTF8Encoding(false), 1024, true)

            use jtw = new JsonTextWriter(sw, Formatting = Formatting.None)
            do! content.WriteToAsync(jtw)
            do! jtw.FlushAsync()
        }
        :> _

    override this.TryComputeLength(length: byref<int64>) : bool =
        length <- -1L
        false
