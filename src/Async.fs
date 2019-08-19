// Copyright 2019 Cognite AS

namespace Oryx

/// Async extensions
module Async =
    /// Transform the asynchronous value.
    let map f asyncX = async {
        let! x = asyncX
        return f x
    }

    /// Create async value from synchronous value.
    let inline single x = async {
        return x
    }
