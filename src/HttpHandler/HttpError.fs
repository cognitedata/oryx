namespace Oryx

open System

type HttpError =
    {
        /// The current context, useful for e.g logging
        Context: HttpContext
        /// Exception if any
        Exception: Exception option
    }


exception HttpException of HttpError

/// Skip exception will not be recorded and forwarded by `choose`.
module HttpError =
    let failwith (err: HttpError) = raise (HttpException err)
