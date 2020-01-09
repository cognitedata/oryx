namespace Oryx.Protobuf

open Oryx

module ResponseReaders =
    let protobuf<'b, 'r, 'err> = parse<'b, 'r, 'err>
