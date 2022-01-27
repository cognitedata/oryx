// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System.Collections.Generic

/// Pre-defined events. Just a string so custom HttpHandlers are free to add their own events.
module Metric =
    [<Literal>]
    let FetchInc = "MetricFetchInc"

    [<Literal>]
    let FetchErrorInc = "MetricFetchErrorInc"

    [<Literal>]
    let FetchRetryInc = "MetricFetchRetryInc"

    [<Literal>]
    let FetchLatencyUpdate = "MetricFetchLatencyUpdate"

    [<Literal>]
    let DecodeErrorInc = "MetricDecodeErrorInc"

type IMetrics =
    abstract Counter: metric: string -> labels: IDictionary<string, string> -> increase: int64 -> unit
    abstract Gauge: metric: string -> labels: IDictionary<string, string> -> value: float -> unit

type EmptyMetrics() =
    interface IMetrics with
        member _.Counter _ _ _ = ()
        member _.Gauge _ _ _ = ()
