// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Tests

[<RequireQualifiedAccess>]
module Result =
    let isOk =
        function
        | Ok _ -> true
        | _ -> false

    let isError res = not (isOk res)
