module ProjectMemory.Similarity

open System

let private tokenize (s: string) =
    s.ToLowerInvariant().Split([| ' '; '\t'; '\n'; '\r'; ','; '.'; ';'; ':' |], StringSplitOptions.RemoveEmptyEntries)
    |> Set.ofArray

let jaccard (a: string) (b: string) =
    let setA = tokenize a
    let setB = tokenize b
    if Set.isEmpty setA && Set.isEmpty setB then 1.0
    elif Set.isEmpty setA || Set.isEmpty setB then 0.0
    else
        let intersection = Set.intersect setA setB |> Set.count |> float
        let union = Set.union setA setB |> Set.count |> float
        intersection / union
