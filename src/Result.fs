// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

[<RequireQualifiedAccess>]
module Result =
    let (>>=) r f = Result.bind f r
    let rtn v = Ok v

    let traverseList f ls =
        let folder head tail =
            f head
            >>= (fun h -> tail >>= (fun t -> h :: t |> rtn))

        List.foldBack folder ls (rtn List.empty)

    let sequenceList ls = traverseList id ls
