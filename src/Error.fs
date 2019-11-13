// Copyright 2019 Cognite AS

namespace Oryx

exception JsonDecodeException of string

type HandlerError<'err> =
    /// Request failed with an exception.
    | Panic of exn
    /// User defined error type.
    | ApiError of 'err

