// Copyright 2019 Cognite AS

namespace Oryx

type Counter = int64 -> unit

type Gauge = int64 -> unit

type IMetrics =
    abstract member TraceFetchInc : Counter
    abstract member TraceFetchErrorInc : Counter
    abstract member TraceFetchRetryInc : Counter
    abstract member TraceFetchLatencyUpdate : Gauge
    abstract member TraceDecodeErrorInc : Counter

type EmptyMetrics () =
    interface IMetrics with
        member this.TraceFetchInc = ignore
        member this.TraceFetchErrorInc = ignore
        member this.TraceFetchRetryInc = ignore
        member this.TraceFetchLatencyUpdate = ignore
        member this.TraceDecodeErrorInc = ignore
