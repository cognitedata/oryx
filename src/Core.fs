// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System.Threading.Tasks

open FSharp.Control.TaskBuilder
open FsToolkit.ErrorHandling

type IHttpNext<'TSource> =
    abstract member OnSuccessAsync: ctx: HttpContext * content: 'TSource -> Task<unit>
    abstract member OnErrorAsync: ctx: HttpContext * error: exn -> Task<unit>
    abstract member OnCancelAsync: ctx: HttpContext -> Task<unit>

type HttpHandler<'TResult> = IHttpNext<'TResult> -> Task<unit>
