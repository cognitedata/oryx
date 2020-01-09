namespace Oryx.Protobuf

open System.IO
open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks.V2.ContextInsensitive
open Oryx

module ResponseReaders =
    let protobuf<'b, 'r, 'err> (parser : Stream -> 'b) (next: NextFunc<'b, 'r, 'err>) (context : Context<HttpResponseMessage>) : Task<Result<Context<'r>,HandlerError<'err>>> =
        task {
            let response = context.Response
            let! stream = response.Content.ReadAsStreamAsync ()
            try
                let b = parser stream
                return! next { Request = context.Request; Response = b }
            with
            | ex -> return Error (Panic ex)
        }
