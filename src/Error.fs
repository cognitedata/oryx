// Copyright 2019 Cognite AS

namespace Oryx

exception JsonDecodeException of string

type HandlerError<'err> =
    /// Request failed with some exception, e.g HttpClient throws an exception, or JSON decode error.
    | Panic of exn
    /// User defined error response.
    | ResponseError of 'err

