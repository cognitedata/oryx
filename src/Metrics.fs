// Copyright 2020 Cognite AS

namespace Oryx

open System.Collections.Generic

/// Pre-defined events. Just a string so custom HttpHandlers are free to add their own events.
module Metric =
    let [<Literal>] FetchInc = "MetricFetchInc"
    let [<Literal>] FetchErrorInc = "MetricFetchErrorInc"
    let [<Literal>] FetchRetryInc = "MetricFetchRetryInc"
    let [<Literal>] FetchLatencyUpdate = "MetricFetchLatencyUpdate"
    let [<Literal>] DecodeErrorInc = "MetricDecodeErrorInc"

type IMetrics =
    abstract member Counter : metric: string -> labels: IDictionary<string, string> -> increase: int64 -> unit
    abstract member Gauge : metric: string -> labels: IDictionary<string, string> -> value: float -> unit

type EmptyMetrics () =
    interface IMetrics with
        member _.Counter _ _ _ = ()
        member _.Gauge _ _ _ = ()
