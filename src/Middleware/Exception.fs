// Copyright 2020 Cognite AS
// SPDX-License-Identifier: Apache-2.0

namespace Oryx

open System

/// Skip exception will not be recorded and forwarded by `choose`.
exception SkipException of string with
    static member Create() = SkipException String.Empty

/// Wrapping an exception as a PanicException will short-circuit the
/// handlers. A PanicException cannot be catched by `catch` and will
/// not be skipped by `choose`
exception PanicException of exn with
    /// Ensures that the exception is a `PanicException`, but will not
    /// wrap a `PanicException` in another `PanicException`.

    override this.ToString () =
        match this :> exn with
        | PanicException(err) -> err.ToString ()
        | _ -> "PanicException"

    static member Ensure(error) =
        match error with
        | PanicException _ -> error
        | _ -> PanicException error
