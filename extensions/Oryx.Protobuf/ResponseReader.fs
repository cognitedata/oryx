namespace Oryx.Protobuf

open Oryx

module ResponseReader =
    let protobuf<'b, 'r, 'err> = parse<'b, 'r, 'err>
