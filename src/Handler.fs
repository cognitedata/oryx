namespace Oryx

open System
open System.Net.Http

open Microsoft.FSharp.Data.UnitSystems.SI


[<Measure>] type ms

type NextHandler<'a, 'b> = Context<'a> -> Async<Context<'b>>

type HttpHandler<'a, 'b, 'c> = NextHandler<'b, 'c> -> Context<'a> -> Async<Context<'c>>

type HttpHandler<'a, 'b> = HttpHandler<'a, 'a, 'b>

type HttpHandler<'a> = HttpHandler<HttpResponseMessage, 'a>

type HttpHandler = HttpHandler<HttpResponseMessage>

[<AutoOpen>]
module Handler =

    /// **Description**
    ///
    /// Run the handler in the given context.
    ///
    /// **Parameters**
    ///   * `handler` - parameter of type `HttpHandler<'a,'b,'b>`
    ///   * `ctx` - parameter of type `Context<'a>`
    ///
    /// **Output Type**
    ///   * `Async<Result<'b,ResponseError>>`
    ///
    let runHandler (handler: HttpHandler<'a,'b,'b>) (ctx : Context<'a>) : Async<Result<'b, ResponseError>> =
        async {
            let! a = handler Async.single ctx
            return a.Result
        }

    let private secondsInMilliseconds = 1000<ms/UnitSymbols.s>  // relation between seconds and millisecond

    [<Literal>]
    let DefaultInitialRetryDelay = 150<ms>
    [<Literal>]
    let DefaultMaxBackoffDelay = 120<UnitSymbols.s>

    let rand = System.Random ()

    let bind fn ctx =
        match ctx.Result with
        | Ok res ->
            fn res
        | Error err ->
            { Request = ctx.Request; Result = Error err }

    let bindAsync (fn: Context<'a> -> Async<Context<'b>>) (a: Async<Context<'a>>) : Async<Context<'b>> =
        async {
            let! p = a
            match p.Result with
            | Ok _ ->
                return! fn p
            | Error err ->
                return { Request = p.Request; Result = Error err }
        }

    let compose (first : HttpHandler<'a, 'b, 'd>) (second : HttpHandler<'b, 'c, 'd>) : HttpHandler<'a,'c,'d> =
        fun (next: NextHandler<_, _>) (ctx : Context<'a>) ->
            let next' = second next
            let next'' = first next'

            next'' ctx

    let (>>=) a b =
        bindAsync b a

    let (>=>) a b =
        compose a b

    // https://fsharpforfunandprofit.com/posts/elevated-world-4/
    let traverseContext fn (list : Context<'a> list) =
        // define the monadic functions
        let (>>=) ctx fn = bind fn ctx

        let retn a =
            { Request = Context.defaultRequest; Result = Ok a }

        // define a "cons" function
        let cons head tail = head :: tail

        // right fold over the list
        let initState = retn []
        let folder head tail =
            fn head >>= (fun h ->
                tail >>= (fun t ->
                    retn (cons h t)
                )
            )

        List.foldBack folder list initState

    let sequenceContext (ctx : Context<'a> list) : Context<'a list> = traverseContext id ctx

    let shouldRetry (err: ResponseError) =
        let retryCode =
            match err.Code with
            // @larscognite: Retry on 429,
            | 429 -> true
            // and I would like to say never on other 4xx, but we give 401 when we can't authenticate because
            // we lose connection to db, so 401 can be transient
            | 401 -> true
            // 500 is hard to say, but we should avoid having those in the api
            | 500 ->
              true // we get random and transient 500 responses often enough that it's worth retrying them.
            // 502 and 503 are usually transient.
            | 502 -> true
            | 503 -> true
            // do not retry other responses.
            | _ -> false

        let retryEx =
            if err.InnerException.IsSome then
                match err.InnerException.Value with
                | :? Net.Http.HttpRequestException
                | :? System.Net.WebException -> true
                // do not retry other exceptions.
                | _ -> false
            else
                false

        // Retry if retriable code or retryable exception
        retryCode || retryEx

    /// Retries the given HTTP handler up to `maxRetries` retries with
    /// exponential backoff and up to 2 minute with randomness.
    let rec retry (initialDelay: int<ms>) (maxRetries : int) (handler: HttpHandler<'a,'b,'c>) (next: NextHandler<'b,'c>) (ctx: Context<'a>) : Async<Context<'c>> = async {
        let exponentialDelay = min (secondsInMilliseconds * DefaultMaxBackoffDelay / 2) (initialDelay * 2)
        let randomDelayScale = min (secondsInMilliseconds * DefaultMaxBackoffDelay / 2) (initialDelay * 2)
        let nextDelay = rand.Next(int randomDelayScale) * 1<ms> + exponentialDelay

        let! ctx' = handler next ctx

        match ctx'.Result with
        | Ok _ -> return ctx'
        | Error err ->
            if shouldRetry err && maxRetries > 0 then
                do! int initialDelay |> Async.Sleep
                return! retry nextDelay (maxRetries - 1) handler next ctx
            else
                return ctx'
    }

    /// Run list of HTTP handlers concurrently.
    let concurrent (handlers : HttpHandler<'a, 'b, 'b> seq) (next: NextHandler<'b list, 'c>) (ctx: Context<'a>) : Async<Context<'c>> = async {
        let! res =
            Seq.map (fun handler -> handler Async.single ctx) handlers
            |> Async.Parallel
            |> Async.map List.ofArray

        return! next (res |> sequenceContext)
    }
