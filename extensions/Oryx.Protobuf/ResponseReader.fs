namespace Oryx.Protobuf

open Oryx

module ResponseReader =
    let protobuf<'T, 'TResult, 'TError> = parse<'T, 'TResult, 'TError>
