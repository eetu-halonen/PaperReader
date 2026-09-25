/// Builds the narration script from analysed source units, either offline or with a Mistral model.
module PaperReader.Core.Narration

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// A narrated line before indexes, pauses and visuals are assigned.
type Draft = { Src: SourceUnit; Say: string; Show: string option }

let private pauseFor (u: SourceUnit) (lastOfUnit: bool) =
    match u.Kind with
    | UnitKind.Title -> 900
    | UnitKind.Heading -> 700
    | UnitKind.Equation when lastOfUnit -> 900
    | _ when lastOfUnit && u.ParagraphEnd -> 450
    | _ -> 120

let private eqRefRx = Regex(@"\b(?:Equations?|Eqs?\.?|Formula)\s*\(?([A-Z]?\.?\d+(?:\.\d+)?[a-z]?)\)?", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Splits long narration so each clip stays well inside the TTS input limit.
let private splitLong (s: string) =
    if s.Length <= 450 then [ s ]
    else
        let parts = Regex.Split(s, @"(?<=[.!?;])\s+")
        let out = Collections.Generic.List<string>()
        let cur = Text.StringBuilder()
        for p in parts do
            if cur.Length > 0 && cur.Length + p.Length > 400 then
                out.Add(cur.ToString().Trim())
                cur.Clear() |> ignore
            cur.Append(p).Append(' ') |> ignore
        if cur.Length > 0 then out.Add(cur.ToString().Trim())
        List.ofSeq out

/// Turns drafts into the final script: indexes, pauses, and which image is on screen for each clip.
let finalize (a: Analysis) (narrator: string) (drafts: Draft list) : Script =
    let visuals = a.Visuals |> Array.map (fun v -> v.Id, v) |> dict
    let byNumber =
        a.Visuals
        |> Array.choose (fun v -> v.EqNumber |> Option.map (fun n -> n, v.Id))
        |> Array.collect (fun (n, id) ->
            // "4–6" makes 4, 5 and 6 point at the same image
            match n.Split '–' with
            | [| x; y |] ->
                match Int32.TryParse x, Int32.TryParse y with
                | (true, a), (true, b) when b >= a && b - a < 10 -> [| for k in a .. b -> string k, id |]
                | _ -> [| x, id; y, id |]
            | _ -> [| n, id |])
        |> Array.distinctBy fst
        |> dict
    let expanded =
        drafts
        |> List.collect (fun d -> splitLong d.Say |> List.map (fun s -> { d with Say = s }))
        |> List.filter (fun d -> d.Say |> Seq.exists Char.IsLetterOrDigit)
        |> Array.ofList
    let mutable recent: (string * int * int) option = None // visual, section, clips left
    let segments =
        expanded
        |> Array.mapi (fun i d ->
            let lastOfUnit = i = expanded.Length - 1 || expanded.[i + 1].Src.Id <> d.Src.Id
            let u = d.Src
            if u.Kind = UnitKind.Heading || u.Kind = UnitKind.Title then recent <- None
            let valid (id: string) = visuals.ContainsKey id
            let shown, reason =
                let own = u.Visual |> Option.filter valid
                match d.Show |> Option.filter valid with
                | Some v when Some v = own || u.Kind = UnitKind.Equation -> Some v, ShowReason.Own
                | Some v -> Some v, ShowReason.Reference
                | None ->
                    match own with
                    | Some v -> Some v, ShowReason.Own
                    | None ->
                        let referenced =
                            eqRefRx.Matches(d.Say)
                            |> Seq.tryPick (fun m ->
                                match byNumber.TryGetValue m.Groups.[1].Value with
                                | true, id -> Some id
                                | _ -> None)
                        match referenced with
                        | Some v -> Some v, ShowReason.Reference
                        | None ->
                            match recent with
                            | Some (v, sec, left) when sec = u.Section && left > 0 && u.Kind = UnitKind.Sentence ->
                                recent <- Some(v, sec, left - 1)
                                Some v, ShowReason.Recent
                            | _ -> None, ShowReason.Own
            match shown with
            | Some v when visuals.[v].Kind <> VisualKind.Inline && reason <> ShowReason.Recent ->
                // keep an equation up while the next few sentences explain it
                recent <- Some(v, u.Section, 3)
            | _ -> ()
            { Index = i
              Kind = u.Kind
              Say = d.Say
              Show = shown
              Reason = reason
              Section = u.Section
              Page = u.Page
              PauseAfterMs = pauseFor u lastOfUnit })
    let sections =
        a.Sections
        |> Array.mapi (fun k title ->
            let first = segments |> Array.tryFindIndex (fun s -> s.Section >= k) |> Option.defaultValue (max 0 (segments.Length - 1))
            { Title = title; FirstSegment = first })
        |> Array.filter (fun s -> segments.Length > 0)
    { Version = Script.currentVersion
      Title = a.Title
      PageCount = a.PageCount
      Narrator = narrator
      Sections = sections
      Segments = segments
      Visuals = a.Visuals }

/// Offline narration: the analyser's own spoken text.
let localDrafts (units: SourceUnit seq) =
    units |> Seq.map (fun u -> { Src = u; Say = u.Spoken; Show = u.Visual }) |> List.ofSeq

let buildLocal (a: Analysis) = finalize a "offline" (localDrafts a.Units)

// ---------------------------------------------------------------------------------------------
// Mistral narration
// ---------------------------------------------------------------------------------------------

let systemPrompt = """You write the narration script for a phone app that reads scientific papers aloud. A text-to-speech voice speaks your text while the listener watches the phone, where images of the paper's equations appear.

You receive the text of part of a paper, extracted from a PDF, as numbered source units in reading order:
- [T#] the title, [H#] a section heading,
- [S#] a sentence. Extraction garbles inline math: subscripts appear as x_{i}, superscripts as x^{2}, symbols may be missing or odd.
- [E#] a display equation. Its image is attached right after it; the extracted text is only a rough hint.

Write segments that narrate every unit, in order. Rules:
1. Be faithful. Narrate every sentence; do not summarise, skip, reorder, or add claims. Rephrase only as much as needed for listening; keep the authors' wording otherwise.
2. Read inline math the way a lecturer says it aloud: "x sub i", "theta transpose x", "the norm of w, squared", "the sum over i from 1 to n of ...". Use the context to repair garbled extraction.
3. For each [E#] write one segment (or a few) with "src" and "show" set to that id. Start with "Equation N." if it is numbered (never read the number in brackets); for an unnumbered one just say what it states. Then say what it states: read it fully in words when it is short; when it is long, walk through it clearly, left side then right side, without skipping terms. Use the image; trust it over the extracted text.
   For an ALGORITHM unit: read its caption, then walk through the steps in order, briefly, reading the math in words.
4. When a sentence explains or refers to an equation (for example "where x is ..." right after it, or "as in Equation 3"), set "show" to that equation's id so the listener can look at it. Earlier equations are listed under KNOWN EQUATIONS.
   If an [E#] is not a real equation (a table cell, a figure label), output it with "say": "" so it is skipped.
5. Drop citation markers like [12] or (Smith et al., 2020) unless the authors are the subject of the sentence; drop footnote marks, and say "a link" for URLs. Skip stray text from inside figures or tables.
6. Headings: say them as "Section 3. Method." or "Appendix A. Proofs." A title is read as is.
7. Expand abbreviations: e.g. -> for example, i.e. -> that is, et al. -> and colleagues, Fig. -> Figure, Eq. -> Equation, w.r.t. -> with respect to.
8. Each segment is one sentence, or two short ones; at most 60 words. Plain spoken English: no markdown, no LaTeX, no symbols a voice cannot pronounce.

Answer with JSON only: {"segments":[{"src":"S12","say":"...","show":null}]}
"src" is the id of the unit narrated; "show" is an E id or null."""

type private Chunk = { Units: SourceUnit list }

let private chunk (units: SourceUnit[]) : Chunk list =
    let chunks = Collections.Generic.List<Chunk>()
    let cur = Collections.Generic.List<SourceUnit>()
    let mutable chars = 0
    let mutable eqs = 0
    let flush () =
        if cur.Count > 0 then
            chunks.Add { Units = List.ofSeq cur }
            cur.Clear()
            chars <- 0
            eqs <- 0
    for u in units do
        let isEq = u.Kind = UnitKind.Equation
        if cur.Count > 0 && (chars > 6000 || (isEq && eqs >= 6) || (u.Kind = UnitKind.Heading && chars > 2500)) then flush ()
        cur.Add u
        chars <- chars + u.Text.Length + 8
        if isEq then eqs <- eqs + 1
    flush ()
    List.ofSeq chunks

let private label (u: SourceUnit) (visuals: Collections.Generic.IDictionary<string, Visual>) =
    match u.Kind with
    | UnitKind.Title -> sprintf "[%s] TITLE: %s" u.Id u.Text
    | UnitKind.Heading -> sprintf "[%s] HEADING: %s" u.Id u.Text
    | UnitKind.Sentence -> sprintf "[%s] %s" u.Id u.Text
    | UnitKind.Equation when (u.Visual |> Option.exists (fun v -> visuals.ContainsKey v && visuals.[v].Kind = VisualKind.Algorithm)) ->
        sprintf "[%s] ALGORITHM (pseudo-code listing with its caption). Extracted text: %s\n(image of %s follows)" u.Id (u.Text.Replace("\n", " / ")) u.Id
    | UnitKind.Equation ->
        let num =
            match u.Visual |> Option.bind (fun v -> match visuals.TryGetValue v with | true, x -> x.EqNumber | _ -> None) with
            | Some n -> sprintf " numbered (%s)" n
            | None -> " unnumbered"
        sprintf "[%s] EQUATION%s. Extracted text: %s\n(image of %s follows)" u.Id num (u.Text.Replace("\n", " ")) u.Id

let private cleanSay (s: string) =
    let s = Regex.Replace(s, @"[*_#`$\\]", " ")
    MathText.tidy s

/// Set by development tools to see why a chunk fell back to offline narration.
let mutable trace: (string -> unit) option = None
let private log (s: string) = trace |> Option.iter (fun t -> t s)

/// Parses the model's JSON; returns None if it is unusable for this chunk.
let private parseChunk (json: string) (c: Chunk) (knownVisuals: Collections.Generic.ISet<string>) : Draft list option =
    try
        let json =
            let a = json.IndexOf '{'
            let b = json.LastIndexOf '}'
            if a >= 0 && b > a then json.Substring(a, b - a + 1) else json
        use d = JsonDocument.Parse json
        let units = c.Units |> List.map (fun u -> u.Id, u) |> dict
        let order = c.Units |> List.mapi (fun i u -> u.Id, i) |> dict
        let mutable last = c.Units.Head
        let drafts =
            [ for s in d.RootElement.GetProperty("segments").EnumerateArray() do
                  let str (p: string) =
                      match s.TryGetProperty p with
                      | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                      | _ -> None
                  let src =
                      match str "src" |> Option.map (fun x -> x.Trim('[', ']', ' ')) with
                      | Some id when units.ContainsKey id && order.[id] >= order.[last.Id] -> units.[id]
                      | _ -> last
                  last <- src
                  let say = str "say" |> Option.map cleanSay |> Option.defaultValue ""
                  let show = str "show" |> Option.map (fun x -> x.Trim('[', ']', ' ')) |> Option.filter knownVisuals.Contains
                  yield { Src = src; Say = say; Show = show } ]
        // Guard against the model summarising: most sentences and every equation must be narrated.
        let covered = drafts |> List.map (fun d -> d.Src.Id) |> set
        let needed = c.Units |> List.filter (fun u -> u.Kind = UnitKind.Sentence)
        let coveredSentences = needed |> List.filter (fun u -> covered.Contains u.Id) |> List.length
        let eqsOk = c.Units |> List.filter (fun u -> u.Kind = UnitKind.Equation) |> List.forall (fun u -> covered.Contains u.Id)
        let drafts = drafts |> List.filter (fun d -> d.Say <> "")
        if drafts.IsEmpty then log "chunk: no segments"; None
        elif needed.Length > 0 && float coveredSentences < 0.7 * float needed.Length then
            log (sprintf "chunk %s: covered %d of %d sentences" c.Units.Head.Id coveredSentences needed.Length); None
        else
            // An equation the model skipped still gets its offline cue, spliced in at its place.
            let missing = c.Units |> List.filter (fun u -> u.Kind = UnitKind.Equation && not (covered.Contains u.Id))
            if not eqsOk then log (sprintf "chunk %s: spliced offline cues for %A" c.Units.Head.Id (missing |> List.map (fun u -> u.Id)))
            let withCues =
                missing
                |> List.fold (fun (ds: Draft list) (u: SourceUnit) ->
                    let cue = { Src = u; Say = u.Spoken; Show = u.Visual }
                    let before, after = ds |> List.partition (fun d -> order.[d.Src.Id] < order.[u.Id])
                    before @ [ cue ] @ after) drafts
            Some withCues
    with e ->
        log (sprintf "chunk %s: unparsable answer (%s): %s" c.Units.Head.Id e.Message (json.Substring(0, min 300 json.Length)))
        None

type NarrationProgress = { Done: int; Total: int; FellBack: int }

/// Narrates with a Mistral chat model, several chunks in parallel. Chunks the model fails on fall back to
/// the offline narration, so a flaky network never loses part of the paper.
let buildWithMistral
    (key: string)
    (model: string)
    (a: Analysis)
    (image: Visual -> byte[] option)
    (progress: NarrationProgress -> unit)
    (ct: CancellationToken)
    : Task<Script * string option> =
    task {
        let visuals = a.Visuals |> Array.map (fun v -> v.Id, v) |> dict
        let known = Collections.Generic.HashSet<string>(a.Visuals |> Seq.filter (fun v -> v.Kind <> VisualKind.Inline) |> Seq.map (fun v -> v.Id))
        let chunks = chunk a.Units |> Array.ofList
        let results: Draft list option[] = Array.create chunks.Length None
        let mutable finished = 0
        let mutable fellBack = 0
        let mutable firstError: string option = None
        let gate = new SemaphoreSlim(3)
        let knownBefore (c: Chunk) =
            let firstPage = c.Units.Head.Page
            a.Visuals
            |> Array.filter (fun v -> v.Kind = VisualKind.Equation && v.EqNumber.IsSome && v.Rect.Page <= firstPage)
            |> Array.filter (fun v -> not (c.Units |> List.exists (fun u -> u.Id = v.Id)))
            |> Array.map (fun v -> sprintf "%s = Equation (%s)" v.Id v.EqNumber.Value)
        let run (i: int) =
            task {
                do! gate.WaitAsync(ct)
                try
                    let c = chunks.[i]
                    let parts = Collections.Generic.List<Mistral.Part>()
                    let header =
                        let k = knownBefore c
                        let sectionTitle = a.Sections.[min (a.Sections.Length - 1) c.Units.Head.Section]
                        sprintf "PAPER: %s\nCURRENT SECTION: %s\nKNOWN EQUATIONS: %s\n\nSOURCE UNITS:\n"
                            a.Title sectionTitle (if k.Length = 0 then "none" else String.Join("; ", k))
                    let text = Text.StringBuilder(header)
                    for u in c.Units do
                        text.AppendLine(label u visuals) |> ignore
                        match u.Kind, u.Visual with
                        | UnitKind.Equation, Some v when visuals.ContainsKey v ->
                            match image visuals.[v] with
                            | Some png ->
                                parts.Add(Mistral.Text(text.ToString()))
                                text.Clear() |> ignore
                                parts.Add(Mistral.Png png)
                            | None -> ()
                        | _ -> ()
                    if text.Length > 0 then parts.Add(Mistral.Text(text.ToString()))
                    let mutable attempt = 0
                    while results.[i].IsNone && attempt < 3 do
                        attempt <- attempt + 1
                        try
                            let! answer = Mistral.chatJson key model systemPrompt (List.ofSeq parts) ct
                            results.[i] <- parseChunk answer c known
                        with
                        | :? OperationCanceledException -> attempt <- 99
                        | Mistral.MistralError(status, msg) when status = 401 || status = 402 || status = 404 || status = 400 ->
                            if firstError.IsNone then firstError <- Some msg
                            attempt <- 99
                        | e -> if firstError.IsNone then firstError <- Some e.Message
                    if results.[i].IsNone then Interlocked.Increment(&fellBack) |> ignore
                finally
                    gate.Release() |> ignore
                    let d = Interlocked.Increment(&finished)
                    progress { Done = d; Total = chunks.Length; FellBack = fellBack }
            }
        progress { Done = 0; Total = chunks.Length; FellBack = 0 }
        let! _ = Task.WhenAll(chunks |> Array.mapi (fun i _ -> run i))
        ct.ThrowIfCancellationRequested()
        let drafts =
            chunks
            |> Array.mapi (fun i c -> defaultArg results.[i] (localDrafts c.Units))
            |> List.concat
        let narrator = if fellBack = 0 then "mistral:" + model else sprintf "mistral:%s (%d of %d parts offline)" model fellBack chunks.Length
        let warning = if fellBack > 0 then firstError else None
        return finalize a narrator drafts, warning
    }
