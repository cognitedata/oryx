namespace Oryx.Protobuf

open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks

open Google.Protobuf

type ProtobufPushStreamContent (content: IMessage) =
    inherit HttpContent()
    let _content = content
    do base.Headers.ContentType <- MediaTypeHeaderValue "application/protobuf"

    override this.SerializeToStreamAsync(stream: Stream, context: TransportContext): Task =
        content.WriteTo stream |> Task.FromResult :> _

    override this.TryComputeLength(length: byref<int64>): bool =
        length <- -1L
        false
