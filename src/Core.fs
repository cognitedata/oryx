// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System.Threading.Tasks

type IHttpNext<'TSource> =
    abstract member OnSuccessAsync: ctx: HttpContext * content: 'TSource -> Task<unit>
    abstract member OnErrorAsync: ctx: HttpContext * error: exn -> Task<unit>
    abstract member OnCancelAsync: ctx: HttpContext -> Task<unit>

type HttpHandler<'TResult> = IHttpNext<'TResult> -> Task<unit>

exception HttpException of (HttpContext * exn) with
    override this.ToString() =
        match this :> exn with
        | HttpException(_, err) -> err.ToString()
        | _ -> failwith "This should not never happen."

