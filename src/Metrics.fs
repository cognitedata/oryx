// Copyright 2019 Cognite AS

namespace Oryx

type IMetrics =
    abstract member TraceFetchInc : int64 -> unit
    abstract member TraceFetchErrorInc : int64 -> unit
    abstract member TraceFetchRetryInc : int64 -> unit
    abstract member TraceFetchLatencyUpdate : int64 -> unit
    abstract member TraceDecodeErrorInc : int64 -> unit

type EmptyMetrics () =
    interface IMetrics with
        member this.TraceFetchInc _ = ()
        member this.TraceFetchErrorInc _ = ()
        member this.TraceFetchRetryInc _ = ()
        member this.TraceFetchLatencyUpdate _ = ()
        member this.TraceDecodeErrorInc _ = ()
