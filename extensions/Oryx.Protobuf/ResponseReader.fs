namespace Oryx.Protobuf

open Oryx

module ResponseReader =
    let protobuf<'TResult> = parse<'TResult>
