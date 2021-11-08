// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System


[<RequireQualifiedAccess>]
type ServiceError =
    | Panic of exn: Exception
    | Error of inner: Exception
    | Skip of msg : string

exception ServiceException of ServiceError

module ServiceError =
    let panic (err: Exception) = raise (ServiceException (ServiceError.Panic err))
    let error (err: Exception) = raise (ServiceException (ServiceError.Error err))
    let skip (msg: string) = raise (ServiceException (ServiceError.Skip msg))


(*
/// Wrapping an exception as a PanicException will short-circuit the
/// handlers. A PanicException cannot be catched by `catch` and will
/// not be skipped by `choose`
exception PanicException of exn with
    /// Ensures that the exception is a `PanicException`, but will not
    /// wrap a `PanicException` in another `PanicException`.
    static member Ensure(error) =
        match error with
        | PanicException _ -> error
        | _ -> PanicException error
*)
