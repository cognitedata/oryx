
type FilterFn (fn:seq<obj> -> seq<obj>) =
    member x.__call__<'T>(source: seq<'T>) : seq<'T> =
        fn(source |> Seq.map (fun x -> x :> obj)) |> Seq.map (fun x -> x :?> 'T)

let take(count: int) : FilterFn =
    let _take(source: seq<'TSource>) : seq<'TSource> =
        source
    FilterFn(_take)
