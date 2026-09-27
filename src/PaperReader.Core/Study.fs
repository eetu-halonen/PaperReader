/// Studying a paper with a tutor, by ear. The paper's narration plays section by section; between sections a tutor
/// (the model, with Ask's system prompt: the whole paper, every equation, figure and table) speaks: background the
/// next section needs before it, and after it a short lesson on each idea it covered and a question, answered aloud or
/// with a tap. What the learner knows is kept across papers (Knowledge), so a new paper skips the ideas already known,
/// or checks them with one quick question when they are fading.
///
/// It follows what is known to make learning last rather than to feel easy:
/// - small steps: a section at a time, background the learner lacks taught before it (segmenting, pre-training);
/// - each explanation tied to the equation or figure on screen, with a concrete example (dual coding, worked examples);
/// - retrieval right after: multiple-choice questions whose wrong options are real misconceptions, feedback on every
///   option, and "I don't know" rather than a guess (retrieval practice, feedback);
/// - the idea comes back minutes later in the session, between other ideas, then at growing intervals (spacing,
///   interleaving, FSRS), each time with a different question;
/// - at the end of each part, the learner explains its ideas aloud in their own words and gets feedback against the
///   paper (generation, elaboration);
/// - known ideas are tested, never assumed: skipping a lesson asks its question first.
module PaperReader.Core.Study

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

// ---------------------------------------------------------------------------------------------
// The plan, the lessons, the learner's progress
// ---------------------------------------------------------------------------------------------

/// One idea to learn: small enough to explain in a couple of minutes and check with one question.
type Idea =
    { /// "c1", "c2", ... in teaching order.
      Id: string
      Name: string
      /// What the learner will understand.
      Goal: string
      /// One sentence that makes sense without the paper, kept in what the learner knows.
      Definition: string
      /// Background the paper relies on without explaining it (taught from standard knowledge).
      Background: bool
      /// Central to the paper, rather than supporting.
      Core: bool
      /// Ideas of the plan it builds on.
      Requires: string list
      /// The paper's section it is in (index into Script.Sections), 0 for none.
      Section: int
      /// Equations, figures and tables about it.
      Visuals: string list
      /// Ideas the learner already knows (Concept ids) that it builds on: reminded of, not taught.
      Uses: string list
      /// The learner's idea (Concept id) this is, found when planning and confirmed: skipped or checked quickly.
      Same: string option }

/// The ideas of one part of the paper, and a question to answer in one's own words at its end.
type Part = { Title: string; Ideas: Idea list; Recap: string; Points: string }

type Plan =
    { /// What the paper does and why it matters, in a few sentences.
      Overview: string
      Parts: Part list }

    member this.Ideas = this.Parts |> List.collect (fun p -> p.Ideas)
    member this.Idea(id: string) = this.Ideas |> List.tryFind (fun i -> i.Id = id)
    member this.PartOf(id: string) = this.Parts |> List.tryFindIndex (fun p -> p.Ideas |> List.exists (fun i -> i.Id = id))

/// An idea's lesson: the explanation, an example, and the questions that check it.
type Lesson =
    { /// The equation, figure or table shown with the explanation.
      Show: string option
      Explanation: string
      Example: string
      /// Multiple choice, each from a different angle: the first checks the lesson, the others come later.
      Checks: Question list
      /// Answered from memory in reviews.
      Recall: Question option }

    member this.Questions = this.Checks @ Option.toList this.Recall

[<RequireQualifiedAccess>]
type Outcome =
    /// Taught and checked in this paper.
    | Learned
    /// "I know this", and the question showed it.
    | TestedOut
    /// Known from another paper and fresh: skipped.
    | Known
    /// Known from another paper; a quick question showed it still is.
    | Refreshed

/// How far the learner is in a paper's plan.
type Progress =
    { /// Idea of the plan -> the learner's idea (Concept id) it is: known when planning, matched later, or made
      /// when it was first checked.
      Links: Map<string, string>
      Done: Map<string, Outcome>
      /// Parts whose recap was answered or skipped.
      Recaps: Set<int>
      /// Known ideas the learner wants taught anyway, or that a quick question showed are forgotten.
      Teach: Set<string>
      /// Questions the learner reported as wrong: never asked again.
      Reported: Set<string>
      /// When the plan was last matched against what the learner knows.
      Matched: DateTime
      /// The first segment of the narration not yet heard in the session.
      Heard: int
      /// Where the tutor is (a lesson, or a question not answered yet), so the session comes back to the same place.
      At: string }

module Progress =
    let empty (now: DateTime) =
        { Links = Map.empty; Done = Map.empty; Recaps = Set.empty; Teach = Set.empty; Reported = Set.empty; Matched = now; Heard = 0; At = "" }

/// A stretch of the narration and the tutor's part around it: the background ideas taught before it is heard, and
/// the paper's ideas it covers, taught and checked right after it.
type Stop =
    { /// The narration heard in it: first and last segment.
      First: int
      Last: int
      Before: Idea list
      After: Idea list }

/// What to do next in a study session.
[<RequireQualifiedAccess>]
type Step =
    /// Hear the paper's narration from one segment to another (inclusive).
    | Listen of first: int * last: int
    /// Explain an idea of the plan, then check it.
    | Teach of idea: string
    /// An idea the learner knows but may be forgetting: one question; right skips it, wrong teaches it.
    | Check of idea: string * concept: string
    /// An idea learned minutes ago, asked again between the others.
    | Review of concept: string
    /// Explain a part's ideas in one's own words.
    | Recap of part: int
    | Finished

let private learning (m: Memory) = m.Stage = CardStage.Learning || m.Stage = CardStage.Relearning

/// Where the narration covers an idea: where its first equation or figure is read, or where its section starts.
let segmentOf (script: Script) (idea: Idea) : int option =
    let order = Narration.equationOrder script
    idea.Visuals
    |> List.tryPick (fun v -> order |> Array.tryFind (fun (x, _) -> x.Id = v) |> Option.map snd)
    |> Option.orElse (if idea.Section > 0 && idea.Section < script.Sections.Length then Some script.Sections.[idea.Section].FirstSegment else None)

/// The last segment of a section.
let private sectionEnd (script: Script) (section: int) =
    let n = script.Segments.Length
    if section + 1 < script.Sections.Length then max 0 (min (n - 1) (script.Sections.[section + 1].FirstSegment - 1)) else n - 1

/// The session's stops, in the order of the narration. Each idea of the paper is taught right after the section it is
/// in is heard (one without a section, with the idea before it); each background idea before the stop of the next
/// idea of the paper in the plan, which is the first one that needs it.
let stops (script: Script) (plan: Plan) : Stop list =
    let n = script.Segments.Length
    if n = 0 then []
    else
        let firstEnd = sectionEnd script script.Segments.[0].Section
        let after = Collections.Generic.List<int * Idea>()
        let before = Collections.Generic.List<int * Idea>()
        let waiting = Collections.Generic.List<Idea>()
        let mutable previous = None
        for i in plan.Ideas do
            if i.Background then waiting.Add i
            else
                let section =
                    if i.Section > 0 && i.Section < script.Sections.Length then Some i.Section
                    else segmentOf script i |> Option.map (fun seg -> script.Segments.[seg].Section)
                let e =
                    match section, previous with
                    | Some s, _ -> sectionEnd script s
                    | None, Some e -> e
                    | None, None -> firstEnd
                after.Add((e, i))
                for b in waiting do before.Add((e, b))
                waiting.Clear()
                previous <- Some e
        for b in waiting do before.Add((defaultArg previous firstEnd, b))
        let ends = Seq.append (Seq.map fst after) (Seq.map fst before) |> Seq.distinct |> Seq.sort |> List.ofSeq
        let ends = if List.isEmpty ends || List.last ends < n - 1 then ends @ [ n - 1 ] else ends
        ends
        |> List.mapi (fun k e ->
            { First = (if k = 0 then 0 else ends.[k - 1] + 1)
              Last = e
              Before = [ for (x, i) in before do if x = e then yield i ]
              After = [ for (x, i) in after do if x = e then yield i ] })

/// The stop after which a part's ideas have all been taught, where its recap comes.
let private recapStop (all: Stop list) (part: Part) =
    let ids = part.Ideas |> List.map (fun i -> i.Id) |> Set.ofList
    all
    |> List.indexed
    |> List.filter (fun (_, st) -> st.Before @ st.After |> List.exists (fun i -> ids.Contains i.Id))
    |> List.tryLast
    |> Option.map fst

/// The next step, and the progress with the known ideas passed on the way marked as skipped. `last` is the idea
/// (Concept id) just asked about, so it isn't asked again straight away.
let next (now: DateTime) (script: Script) (plan: Plan) (progress: Progress) (concepts: Map<string, Concept>) (last: string option) : Step * Progress =
    let dueAgain =
        progress.Links
        |> Map.toSeq
        |> Seq.filter (fun (idea, _) -> progress.Done.ContainsKey idea)
        |> Seq.choose (fun (_, c) -> concepts.TryFind c)
        |> Seq.filter (fun c ->
            Some c.Id <> last && learning c.Memory && c.Memory.Due <= now
            && c.Questions |> List.exists (fun q -> not q.Options.IsEmpty && not (progress.Reported.Contains q.Id)))
        |> Seq.sortBy (fun c -> c.Memory.Due)
        |> Seq.tryHead
    match dueAgain with
    | Some c -> Step.Review c.Id, progress
    | None ->
        let all = stops script plan
        let recapAt = plan.Parts |> List.map (recapStop all)
        let p = ref progress
        let step = ref None
        let visit (ideas: Idea list) =
            for idea in ideas do
                if step.Value.IsNone && not (p.Value.Done.ContainsKey idea.Id) then
                    if p.Value.Teach.Contains idea.Id then step.Value <- Some(Step.Teach idea.Id)
                    else
                        match p.Value.Links.TryFind idea.Id |> Option.bind concepts.TryFind with
                        | Some c when Knowledge.skippable now c -> p.Value <- { p.Value with Done = p.Value.Done.Add(idea.Id, Outcome.Known) }
                        | Some c when c.Memory.Stage <> CardStage.New -> step.Value <- Some(Step.Check(idea.Id, c.Id))
                        | _ -> step.Value <- Some(Step.Teach idea.Id)
        for si, stop in List.indexed all do
            if step.Value.IsNone then
                visit stop.Before
                if step.Value.IsNone && p.Value.Heard <= stop.Last then step.Value <- Some(Step.Listen(max stop.First p.Value.Heard, stop.Last))
                visit stop.After
                for pi, part in List.indexed plan.Parts do
                    // a part whose ideas were all known already needs no recap
                    let learnedHere = part.Ideas |> List.exists (fun i -> p.Value.Done.TryFind i.Id |> Option.exists (fun o -> o <> Outcome.Known))
                    if step.Value.IsNone && recapAt.[pi] = Some si && part.Recap <> "" && learnedHere && not (p.Value.Recaps.Contains pi) then
                        step.Value <- Some(Step.Recap pi)
        defaultArg step.Value Step.Finished, p.Value

/// The ideas to be taught next, in the session's order, for writing their lessons ahead. Known ideas aren't among
/// them: they are only taught if a quick question shows they are forgotten.
let upcoming (script: Script) (plan: Plan) (progress: Progress) (concepts: Map<string, Concept>) (count: int) =
    stops script plan
    |> List.collect (fun st -> st.Before @ st.After)
    |> List.filter (fun i ->
        not (progress.Done.ContainsKey i.Id)
        && (progress.Teach.Contains i.Id
            || (match progress.Links.TryFind i.Id |> Option.bind concepts.TryFind with
                | Some c -> c.Memory.Stage = CardStage.New
                | None -> true)))
    |> List.truncate count
    |> List.map (fun i -> i.Id)

/// Roughly how long what is left takes: the narration not heard yet (150 words a minute), two minutes per idea to
/// learn, one per quick check, two per recap.
let minutesLeft (now: DateTime) (script: Script) (plan: Plan) (progress: Progress) (concepts: Map<string, Concept>) =
    let words =
        script.Segments
        |> Seq.skip (min script.Segments.Length (max 0 progress.Heard))
        |> Seq.sumBy (fun s -> s.Say.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries).Length)
    let ideas =
        plan.Ideas
        |> List.filter (fun i -> not (progress.Done.ContainsKey i.Id))
        |> List.sumBy (fun i ->
            match progress.Links.TryFind i.Id |> Option.bind concepts.TryFind with
            | _ when progress.Teach.Contains i.Id -> 2
            | Some c when Knowledge.skippable now c -> 0
            | Some c when c.Memory.Stage <> CardStage.New -> 1
            | _ -> 2)
    let recaps = plan.Parts |> List.indexed |> List.filter (fun (i, p) -> p.Recap <> "" && not (progress.Recaps.Contains i)) |> List.length
    words / 150 + ideas + 2 * recaps

// ---------------------------------------------------------------------------------------------
// Reading the model's text
// ---------------------------------------------------------------------------------------------

/// A line as a field: "KEY: value", ignoring markdown the model may put around it ("**KEY:**", "- key:").
let private fieldRx = Regex(@"^[\s>#*_-]*([A-Za-z][A-Za-z ]{0,14}?)\s*\d*[*_]*\s*:[*_]*\s*(.*)$", RegexOptions.Compiled)

let private field (line: string) =
    let m = fieldRx.Match line
    if m.Success then Some(m.Groups.[1].Value.Trim().ToUpperInvariant(), m.Groups.[2].Value.Trim()) else None

let private none (s: string) =
    let t = s.Trim().Trim('.', '*', '`', '"').ToLowerInvariant()
    t = "" || t = "none" || t = "null" || t = "-" || t = "n/a"

let private ideaId (s: string) =
    let s = s.Trim().Trim('*', '.', ':', ' ')
    if s <> "" && s |> Seq.forall Char.IsDigit then "c" + s else s.ToLowerInvariant()

let private visualIds (script: Script) (s: string) =
    if none s then []
    else
        [ for x in s.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries) do
              let id = x.Trim('[', ']', '*', '.', '(', ')', '`')
              match script.Visual id with
              | Some v when v.Kind <> VisualKind.Inline -> yield v.Id
              | _ -> () ]
        |> List.distinct

let private ideaHeader = Regex(@"^[\s>#*_-]*IDEA\s+([A-Za-z0-9_]+)[*_]*\s*[:.)\-—–]*[*_]*\s*(.*)$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Reads a plan; ideas without a name, and parts without ideas, are left out.
let parsePlan (script: Script) (text: string) : Plan =
    let parts = ResizeArray<Part>()
    let mutable overview = ""
    let mutable part: Part option = None
    let mutable idea: Idea option = None
    // the field that continues on the next line, if any
    let mutable last = ""
    let seen = Collections.Generic.HashSet<string>()
    let closeIdea () =
        match part, idea with
        | Some p, Some i when i.Name <> "" -> part <- Some { p with Ideas = p.Ideas @ [ i ] }
        | _ -> ()
        idea <- None
    let closePart () =
        closeIdea ()
        match part with
        | Some p when not p.Ideas.IsEmpty -> parts.Add p
        | _ -> ()
        part <- None
    let setIdea f = idea <- idea |> Option.map f
    let append (s: string) (more: string) = if s = "" then more else s + " " + more
    for raw in text.Replace("\r", "").Split('\n') do
        let line = raw.Trim()
        let header = ideaHeader.Match line
        if line = "" then last <- ""
        elif header.Success then
            closeIdea ()
            if part.IsNone then part <- Some { Title = ""; Ideas = []; Recap = ""; Points = "" }
            let mutable id = ideaId header.Groups.[1].Value
            while not (seen.Add id) do id <- id + "b"
            idea <-
                Some
                    { Id = id; Name = header.Groups.[2].Value.Trim().Trim('*', ' '); Goal = ""; Definition = ""; Background = false; Core = false
                      Requires = []; Section = 0; Visuals = []; Uses = []; Same = None }
            last <- ""
        else
            match field line with
            | Some ("OVERVIEW", v) -> overview <- v; last <- "OVERVIEW"
            | Some ("PART", v) ->
                closePart ()
                part <- Some { Title = v.Trim('*', ' '); Ideas = []; Recap = ""; Points = "" }
                last <- ""
            | Some ("RECAP", v) ->
                closeIdea ()
                part <- part |> Option.map (fun p -> { p with Recap = v })
                last <- "RECAP"
            | Some ("POINTS", v) ->
                closeIdea ()
                part <- part |> Option.map (fun p -> { p with Points = v })
                last <- "POINTS"
            | Some ("KIND", v) when idea.IsSome -> setIdea (fun i -> { i with Background = v.ToLowerInvariant().Contains "background" })
            | Some ("CORE", v) when idea.IsSome -> setIdea (fun i -> { i with Core = v.ToLowerInvariant().StartsWith "y" })
            | Some ("REQUIRES", v) when idea.IsSome ->
                let ids = if none v then [] else [ for x in v.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries) -> ideaId x ]
                setIdea (fun i -> { i with Requires = ids |> List.filter (fun r -> r <> i.Id) })
            | Some ("SECTION", v) when idea.IsSome ->
                let m = Regex.Match(v, @"\d+")
                let s = if m.Success then int m.Value else 0
                setIdea (fun i -> { i with Section = (if s > 0 && s < script.Sections.Length then s else 0) })
            | Some ("SHOW", v) when idea.IsSome -> setIdea (fun i -> { i with Visuals = visualIds script v })
            | Some (("SAME AS KNOWN" | "KNOWN"), v) when idea.IsSome -> setIdea (fun i -> { i with Same = (if none v then None else Some(v.Trim('*', '`', ' ', '.'))) })
            | Some ("USES KNOWN", v) when idea.IsSome ->
                setIdea (fun i -> { i with Uses = (if none v then [] else [ for x in v.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries) -> x.Trim('*', '`', '.') ]) })
            | Some ("GOAL", v) when idea.IsSome -> setIdea (fun i -> { i with Goal = v }); last <- "GOAL"
            | Some ("DEFINE", v) when idea.IsSome -> setIdea (fun i -> { i with Definition = v }); last <- "DEFINE"
            | _ ->
                // a long value wrapped onto the next line
                match last with
                | "OVERVIEW" -> overview <- append overview line
                | "RECAP" -> part <- part |> Option.map (fun p -> { p with Recap = append p.Recap line })
                | "POINTS" -> part <- part |> Option.map (fun p -> { p with Points = append p.Points line })
                | "GOAL" -> setIdea (fun i -> { i with Goal = append i.Goal line })
                | "DEFINE" -> setIdea (fun i -> { i with Definition = append i.Definition line })
                | _ -> ()
    closePart ()
    let known = parts |> Seq.collect (fun p -> p.Ideas) |> Seq.map (fun i -> i.Id) |> Set.ofSeq
    { Overview = overview
      Parts =
        [ for p in parts ->
              { p with
                  Title = (if p.Title = "" then "The paper" else p.Title)
                  Ideas = p.Ideas |> List.map (fun i -> { i with Requires = i.Requires |> List.filter known.Contains }) } ] }

/// The ideas written so far while a plan streams in, for showing progress.
let planSoFar (partial: string) : string list =
    [ for raw in partial.Replace("\r", "").Split('\n') do
          let m = ideaHeader.Match(raw.Trim())
          if m.Success && m.Groups.[2].Value.Trim() <> "" then yield m.Groups.[2].Value.Trim().Trim('*', ' ') ]

let private letters = [| 'A'; 'B'; 'C'; 'D'; 'E'; 'F' |]

let private optionRx = Regex(@"^[\s>*_-]*\(?([A-F])[).:][*_]*\s+(.*)$", RegexOptions.Compiled)
let private whyRx = Regex(@"^[\s>*_-]*WHY\s*\(?([A-F])\)?[*_]*\s*[:.)-][*_]*\s*(.*)$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Reads a lesson; also works on a lesson still being written (what has arrived so far).
let parseLesson (script: Script) (paperId: string) (ideaId: string) (text: string) : Lesson =
    let mutable show = None
    let explanation = StringBuilder()
    let example = StringBuilder()
    let checks = ResizeArray<Question>()
    let mutable recall: Question option = None
    let mutable question: Question option = None
    // what the next plain line belongs to
    let mutable into = ""
    let add (sb: StringBuilder) (line: string) =
        if sb.Length > 0 then sb.Append('\n') |> ignore
        sb.Append(line) |> ignore
    let blank = { Id = ""; Prompt = ""; Options = []; Correct = -1; Why = []; Answer = ""; PaperId = paperId; Visual = None; LastAsked = None }
    let closeQuestion () =
        match question with
        | Some q when q.Prompt <> "" && q.Options.Length >= 2 && q.Correct >= 0 && q.Correct < q.Options.Length ->
            let whys = [ for i in 0 .. q.Options.Length - 1 -> if i < q.Why.Length then q.Why.[i] else "" ]
            checks.Add { q with Id = sprintf "%s:%s:%d" paperId ideaId (checks.Count + 1); Why = whys }
        | _ -> ()
        question <- None
    let setWhy (i: int) (text: string) =
        question <-
            question
            |> Option.map (fun q ->
                let why = Array.ofList (q.Why @ List.replicate (max 0 (i + 1 - q.Why.Length)) "")
                why.[i] <- text
                { q with Why = List.ofArray why })
    for raw in text.Replace("\r", "").Split('\n') do
        let line = raw.TrimEnd()
        let why = whyRx.Match line
        let option = optionRx.Match line
        if why.Success && question.IsSome then
            let letter = Char.ToUpperInvariant why.Groups.[1].Value.[0]
            setWhy (Array.IndexOf(letters, letter)) (why.Groups.[2].Value.Trim())
            into <- "WHY" + string letter
        elif option.Success && question.IsSome && into <> "RIGHT" && not (into.StartsWith "WHY") then
            let i = Array.IndexOf(letters, option.Groups.[1].Value.[0])
            question <- question |> Option.map (fun q -> if i = q.Options.Length then { q with Options = q.Options @ [ option.Groups.[2].Value.Trim() ] } else q)
            into <- "OPTION"
        else
            match field line with
            | Some ("SHOW", v) when into = "" -> show <- visualIds script v |> List.tryHead
            | Some ("EXPLAIN", v) -> (if v <> "" then add explanation v); into <- "EXPLAIN"
            | Some ("EXAMPLE", v) -> (if v <> "" then add example v); into <- "EXAMPLE"
            | Some ("QUESTION", v) ->
                closeQuestion ()
                question <- Some { blank with Prompt = v }
                into <- "QUESTION"
            | Some ("WITH", v) when question.IsSome ->
                question <- question |> Option.map (fun q -> { q with Visual = visualIds script v |> List.tryHead })
            | Some ("RIGHT", v) when question.IsSome ->
                let m = Regex.Match(v.ToUpperInvariant(), @"\b([A-F])\b")
                if m.Success then question <- question |> Option.map (fun q -> { q with Correct = Array.IndexOf(letters, m.Groups.[1].Value.[0]) })
                into <- "RIGHT"
            | Some ("RECALL", v) ->
                closeQuestion ()
                recall <- Some { blank with Id = sprintf "%s:%s:r" paperId ideaId; Prompt = v }
                into <- "RECALL"
            | Some ("ANSWER", v) when recall.IsSome ->
                recall <- recall |> Option.map (fun q -> { q with Answer = v })
                into <- "ANSWER"
            | _ ->
                let t = line.Trim()
                match into with
                | "EXPLAIN" -> add explanation line
                | "EXAMPLE" -> add example line
                | "QUESTION" when t <> "" -> question <- question |> Option.map (fun q -> { q with Prompt = q.Prompt + "\n" + t })
                | "RECALL" when t <> "" -> recall <- recall |> Option.map (fun q -> { q with Prompt = q.Prompt + " " + t })
                | "ANSWER" when t <> "" -> recall <- recall |> Option.map (fun q -> { q with Answer = q.Answer + " " + t })
                | w when w.StartsWith "WHY" && t <> "" && question.IsSome ->
                    let i = Array.IndexOf(letters, w.[3])
                    question <- question |> Option.map (fun q -> { q with Why = q.Why |> List.mapi (fun j x -> if j = i then x + " " + t else x) })
                | _ -> ()
    closeQuestion ()
    { Show = show
      Explanation = explanation.ToString().Trim()
      Example = example.ToString().Trim()
      Checks = List.ofSeq checks
      Recall = recall |> Option.filter (fun q -> q.Prompt <> "" && q.Answer <> "") }

/// The recap's verdict on an answer in one's own words.
[<RequireQualifiedAccess>]
type Verdict =
    | GotIt
    | Partly
    | NotYet

type Feedback = { Verdict: Verdict option; Text: string }

/// Reads feedback on a recap answer; also works while it streams in (the verdict line is held back until whole).
let parseFeedback (text: string) : Feedback =
    let text = text.Replace("\r", "").TrimStart()
    let firstLine = text.Split('\n').[0]
    match field firstLine with
    | Some ("VERDICT", v) when text.Contains "\n" ->
        let v = v.ToLowerInvariant()
        let verdict =
            if v.Contains "not" then Some Verdict.NotYet
            elif v.Contains "part" then Some Verdict.Partly
            elif v.Contains "got" || v.Contains "yes" || v.Contains "right" then Some Verdict.GotIt
            else None
        { Verdict = verdict; Text = text.Substring(text.IndexOf '\n' + 1).Trim() }
    | Some ("VERDICT", _) -> { Verdict = None; Text = "" }
    | _ when "VERDICT".StartsWith(firstLine.Trim().TrimStart('*').ToUpperInvariant()) -> { Verdict = None; Text = "" }
    | _ -> { Verdict = None; Text = text.Trim() }

// ---------------------------------------------------------------------------------------------
// What the tutor says, and what the learner answers aloud
// ---------------------------------------------------------------------------------------------

/// A piece of what the tutor says: the text on screen while it is said (with its $LaTeX$), and the words spoken.
type Said = { Show: string; Say: string }

let private sentenceBreak = Regex(@"(?<=[.!?])\s+(?=[""“(*]?[A-Z0-9$])", RegexOptions.Compiled)

/// Text as clips to say one after another: a short first one, so speech starts quickly, then a sentence or a few.
let said (text: string) : Said list =
    let sentences =
        text.Replace("\r", "").Split('\n')
        |> Seq.map (fun p -> p.Trim())
        |> Seq.filter (fun p -> p <> "")
        |> Seq.collect (fun p -> if p.StartsWith "$$" then [| p |] else sentenceBreak.Split p)
        |> Seq.map (fun x -> x.Trim())
        |> Seq.filter (fun x -> x <> "")
    let clips = ResizeArray<string>()
    let current = StringBuilder()
    for x in sentences do
        let limit = if clips.Count = 0 then 140 else 280
        if current.Length > 0 && current.Length + x.Length > limit then
            clips.Add(current.ToString())
            current.Clear() |> ignore
        if current.Length > 0 then current.Append(' ') |> ignore
        current.Append(x) |> ignore
    if current.Length > 0 then clips.Add(current.ToString())
    [ for c in clips do
          let say = Help.spoken c
          if say.Trim() <> "" then yield { Show = c; Say = say } ]

/// A lesson as the tutor says it: which idea, the explanation, then the example.
let lessonSaid (idea: Idea) (lesson: Lesson) : Said list =
    let intro = if idea.Background then sprintf "Some background first: %s." idea.Name else sprintf "Now: %s." idea.Name
    // the idea's name is on screen already
    [ yield! said intro |> List.map (fun x -> { x with Show = "" })
      yield! said lesson.Explanation
      if lesson.Example <> "" then
          yield! said "Here's an example."
          yield! said lesson.Example ]

/// The order a question's options are shown and said in (indices into its options): mixed, so where the right one
/// sits gives nothing away, but always the same for the same question, so it looks the same when it comes back.
let optionOrder (q: Question) : int list =
    // FNV-1a: string hashes in .NET change from run to run
    let mutable h = 2166136261u
    for c in q.Id do
        h <- (h ^^^ uint32 c) * 16777619u
    let rng = Random(int (h &&& 0x7FFFFFFFu))
    [ 0 .. q.Options.Length - 1 ] |> List.sortBy (fun _ -> rng.Next())

/// Options are said and heard by number: letters sound alike (B, D, E), numbers don't.
let private optionName (position: int) = sprintf "option %d" (position + 1)

/// A multiple-choice question as it is read out, its options in the order shown (`order`: indices into the question's).
let questionSaid (q: Question) (order: int list) : Said list =
    let options =
        order
        |> List.mapi (fun pos i -> sprintf "Option %d: %s." (pos + 1) (Help.spoken q.Options.[i]))
        |> String.concat " "
    [ yield! said q.Prompt
      { Show = ""; Say = options } ]

let private verdictWord = Regex(@"^\W*(correct|right|yes|incorrect|wrong|no)\b[\s.:;,!—–-]*", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Why an option is right or wrong, without the "Correct:" or "Wrong:" the model may start it with.
let private whyOf (q: Question) (i: int) =
    if i >= 0 && i < q.Why.Length then
        let w = verdictWord.Replace(q.Why.[i].Trim(), "")
        if w = "" then "" else string (Char.ToUpperInvariant w.[0]) + w.Substring 1
    else ""

/// The feedback said after an answer: right or not, and why (for a wrong one, also the right answer and why).
let feedbackSaid (q: Question) (order: int list) (choice: int option) : Said list =
    let position i = order |> List.tryFindIndex ((=) i) |> Option.defaultValue i
    let rightOne = sprintf "The answer is %s: %s." (optionName (position q.Correct)) q.Options.[q.Correct]
    let text =
        match choice with
        | Some c when c = q.Correct -> "Right. " + whyOf q c
        | Some c when c >= 0 && c < q.Options.Length -> sprintf "Not quite. %s %s %s" (whyOf q c) rightOne (whyOf q q.Correct)
        | _ -> sprintf "%s %s" rightOne (whyOf q q.Correct)
    said text

/// What was heard in answer to a multiple-choice question.
[<RequireQualifiedAccess>]
type Heard =
    /// An option, by its position on screen.
    | Option of position: int
    | DontKnow
    | Unclear

/// Number words that are rarely anything else, so they count even inside a sentence ("I'd go for two").
let private numberWords =
    [| set [ "1"; "one"; "first"; "yksi"; "eka"; "ensimmäinen"; "ykkönen" ]
       set [ "2"; "two"; "second"; "kaksi"; "toka"; "toinen"; "kakkonen" ]
       set [ "3"; "three"; "third"; "kolme"; "kolmas"; "kolmonen" ]
       set [ "4"; "four"; "fourth"; "neljä"; "neljäs"; "nelonen" ] |]

/// Also what a number or a letter can come out as when said alone ("to", "tree", "bee").
let private letterWords =
    [| numberWords.[0] + set [ "won"; "a"; "ay"; "eh"; "aa"; "alpha" ]
       numberWords.[1] + set [ "to"; "too"; "b"; "bee"; "be"; "bravo" ]
       numberWords.[2] + set [ "tree"; "c"; "see"; "sea"; "si"; "cee"; "charlie" ]
       numberWords.[3] + set [ "for"; "d"; "dee"; "delta" ] |]

let private fillers =
    set [ "option"; "answer"; "letter"; "the"; "is"; "it"; "its"; "it's"; "s"; "i"; "think"; "say"; "would"; "i'd"; "id"
          "my"; "guess"; "maybe"; "probably"; "um"; "uh"; "er"; "erm"; "hmm"; "mm"; "ok"; "okay"; "so"; "well"; "that"
          "one"; "number"; "vastaus"; "se"; "on"; "kai"; "ehkä"; "luulen"; "että"; "vaihtoehto"; "numero" ]

let private dontKnow =
    Regex(@"\b(i\s*)?(don'?t|do not|dunno)\s+know\b|\bno (idea|clue)\b|\bpass\b|\bskip\b|\ben tiedä\b|\bei (aavistusta|hajuakaan)\b",
          RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

let private words (s: string) =
    Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}']+") |> Array.filter (fun w -> w <> "") |> List.ofArray

let private common =
    set [ "the"; "and"; "for"; "that"; "with"; "from"; "this"; "are"; "was"; "its"; "it's"; "into"; "than"; "then"; "they"
          "their"; "which"; "what"; "each"; "only"; "not"; "but"; "all"; "can"; "has"; "have"; "one"; "you"; "because" ]

/// The words that carry meaning, for matching a spoken answer with an option.
let private meaningful (s: string) = words s |> List.filter (fun w -> w.Length > 2 && not (common.Contains w)) |> set

/// Reads a spoken answer: a number ("two", "option 2", "the second one"), a letter ("B"), "I don't know", or the
/// words of an option. `options` are in the order shown.
let heardAnswer (options: string list) (transcript: string) : Heard =
    let tokens = words transcript
    let letterOf (w: string) = letterWords |> Array.tryFindIndex (fun set -> set.Contains w) |> Option.filter (fun i -> i < options.Length)
    let named =
        // "option b", "answer c", "letter a"
        tokens
        |> List.pairwise
        |> List.tryPick (fun (a, b) -> if a = "option" || a = "answer" || a = "letter" || a = "vaihtoehto" then letterOf b else None)
    let content = tokens |> List.filter (fun w -> not (fillers.Contains w))
    match named with
    | Some i -> Heard.Option i
    | None ->
        match content with
        | [ w ] when (letterOf w).IsSome -> Heard.Option (letterOf w).Value
        | [] ->
            // only fillers, one of which may be the letter ("one", "the first one")
            match tokens |> List.choose letterOf |> List.distinct with
            | [ i ] -> Heard.Option i
            | _ -> Heard.Unclear
        | _ when dontKnow.IsMatch transcript -> Heard.DontKnow
        | _ ->
            // the words of an option
            let said = meaningful transcript
            let scores =
                options
                |> List.map (fun o ->
                    let ws = meaningful o
                    if ws.IsEmpty || said.IsEmpty then 0.0
                    else float (Set.intersect ws said).Count / float (min ws.Count said.Count))
            match scores |> List.indexed |> List.sortByDescending snd with
            | (best, b) :: (_, second) :: _ when b >= 0.5 && b - second >= 0.2 -> Heard.Option best
            | [ (best, b) ] when b >= 0.5 -> Heard.Option best
            | _ when tokens.Length > 8 -> Heard.Unclear
            | _ ->
                // a number in a short sentence; "one" after a number is not one ("the second one")
                let numberOf (w: string) = numberWords |> Array.tryFindIndex (fun set -> set.Contains w)
                let numbers =
                    ("" :: tokens)
                    |> List.pairwise
                    |> List.choose (fun (before, w) -> if w = "one" && (numberOf before).IsSome then None else numberOf w)
                    |> List.filter (fun i -> i < options.Length)
                match List.distinct numbers with
                | [ i ] -> Heard.Option i
                | _ -> Heard.Unclear

// ---------------------------------------------------------------------------------------------
// What the model is asked
// ---------------------------------------------------------------------------------------------

/// Ideas the learner knows are named k1, k2, ... in the plan request (their ids are long).
let aliases (concepts: Concept list) : (string * Concept) list =
    concepts
    |> List.filter (fun c -> c.Memory.Stage <> CardStage.New)
    |> List.sortByDescending (fun c -> c.CreatedUtc)
    |> List.truncate 400
    |> List.rev
    |> List.mapi (fun i c -> sprintf "k%d" (i + 1), c)

let private oneLine (s: string) = Regex.Replace(s, @"\s+", " ").Trim()

let private knownBlock (known: (string * Concept) list) =
    if known.IsEmpty then "Nothing yet: this is the first paper they study here.\n"
    else
        let sb = StringBuilder()
        for alias, c in known do
            sb.AppendFormat("{0}: {1} — {2}\n", alias, oneLine c.Name, oneLine c.Definition) |> ignore
        sb.ToString()

let private taskHeader (what: string) =
    sprintf "TASK: this time don't answer a question. %s Ignore the length rules and the REPLY FORMAT above.\n\n" what

/// The plan request, after Ask's system prompt (so the paper is cached for every request).
let planPrompt (script: Script) (known: (string * Concept) list) =
    taskHeader "Plan a study session in which a tutor teaches this paper to the learner, one idea at a time."
    + $$"""HOW THE SESSION GOES
The learner listens to the paper read aloud, section by section (perhaps while walking). After each section, the tutor
speaks: a short lesson on each idea of the plan that section covered, and a question on it. Background the next section
needs is taught just before it.

HOW TO PLAN
- List the ideas a learner must understand to really know this paper. For a research paper: the problem and why it
  matters, the key idea and how it differs from earlier work, each part of the method (its important equations: what
  they compute and why they have that form), the main results and what they show, and the limitations. For any other
  document (a book, an article, a report, slides, notes): its main ideas, arguments, terms, and key examples.
- Add background ideas the paper relies on without explaining them, only when this learner may not know them (see THE
  LISTENER and WHAT THE LEARNER ALREADY KNOWS), each just before the first idea that needs it.
- One idea each: small enough to explain in about a minute of speech (100 to 150 words) and to check with one
  question. Split bigger ones.
- The paper's order: list the ideas in the order the paper covers them, and give every idea of the paper the section
  where the paper covers it (the most specific one, from SECTIONS). An idea spread over several sections goes with the
  last of them.
- Usually 8 to 20 ideas; up to 30 for a long or dense document. Prefer what an expert would still want to know a year
  from now. Leave out related work, setup trivia and acknowledgements.
- Group the ideas into 2 to 6 parts that follow the paper's sections in order. At the end of each part: a question the learner
  answers from memory in their own words, which connects the part's ideas (why, how they fit together), not a list of
  facts; and the key points of a good answer, as the paper supports them.
- Everything must come from the paper, or for background ideas from standard textbook knowledge. Never invent.

WHAT THE LEARNER ALREADY KNOWS (ideas studied in other papers: id, name, definition)
{{knownBlock known}}
Use it in two ways:
- An idea that builds on one of these lists its id in "uses known": the tutor reminds the learner of it instead of
  teaching it. Don't add a background idea the learner already knows.
- Only when an idea of this paper IS one of these, the same concept (perhaps named differently), so that knowing the
  known one means knowing this idea completely as the paper uses it, write its id in "same as known": the tutor will
  skip it, or check it with one question. An idea that uses, applies, extends or is a special case or part of a known
  one is not the same: an attention mechanism is not the softmax it uses, and a paper's results are not the metric they
  are measured with. When in doubt, it is not the same.

{{Cards.sectionsBlock script}}
FORMAT (plain text in exactly this shape, nothing before or after)
OVERVIEW: <2 or 3 sentences: what the paper does and why it matters>

PART: <title>

IDEA c1: <name, a few words>
kind: <paper, or background>
core: <yes if central to the paper, else no>
requires: <ids of earlier ideas of this plan it builds on, comma-separated, or none>
uses known: <ids from WHAT THE LEARNER ALREADY KNOWS it builds on, comma-separated, or none>
same as known: <the id from WHAT THE LEARNER ALREADY KNOWS that this idea is, or none>
section: <the number from SECTIONS where the paper covers it; none only for background>
show: <ids from VISUALS about it, comma-separated, or none>
goal: <one sentence: what the learner will understand>
define: <one sentence defining it so it makes sense without this paper; for an idea of this paper, name the method
  or paper ("In the Transformer, ...")>

IDEA c2: ...

RECAP: <the question for the end of the part>
POINTS: <the key points of a good answer, in one to three sentences>

PART: ...
"""

let private knownLine = Regex(@"(?im)^([\s>*_-]*(?:same as known|uses known|known)[*_]*\s*:[*_]*\s*)(.*)$", RegexOptions.Compiled)

/// A plan with the ids of known ideas (k1, k2, ...) replaced by the learner's own, so the saved text stands alone.
let resolveKnown (known: (string * Concept) list) (text: string) =
    let byAlias = dict [ for alias, c in known -> alias, c.Id ]
    knownLine.Replace(
        text,
        fun m ->
            let ids = [ for a in Regex.Matches(m.Groups.[2].Value, @"\bk\d+\b", RegexOptions.IgnoreCase) do
                            match byAlias.TryGetValue(a.Value.ToLowerInvariant()) with
                            | true, id -> yield id
                            | _ -> () ]
            m.Groups.[1].Value + (if ids.IsEmpty then "none" else String.Join(", ", ids)))

/// The plan's text with "same as known" kept only for the given ideas.
let keepSame (confirmed: Set<string>) (text: string) =
    let mutable idea = ""
    [ for raw in text.Replace("\r", "").Split('\n') do
          let header = ideaHeader.Match(raw.Trim())
          if header.Success then idea <- ideaId header.Groups.[1].Value
          match field raw with
          | Some (("SAME AS KNOWN" | "KNOWN"), v) when not (none v) && not (confirmed.Contains idea) ->
              yield Regex.Replace(raw, @":.*$", ": none")
          | _ -> yield raw ]
    |> String.concat "\n"

let private planBlock (plan: Plan) (progress: Progress) (current: string) =
    let sb = StringBuilder()
    for pi, part in List.indexed plan.Parts do
        sb.AppendFormat("PART {0}: {1}\n", pi + 1, part.Title) |> ignore
        for idea in part.Ideas do
            let status =
                if idea.Id = current then "   <<< TEACH THIS NOW"
                else
                    match progress.Done.TryFind idea.Id with
                    | Some Outcome.Known
                    | Some Outcome.Refreshed -> "   [known already]"
                    | Some _ -> "   [taught]"
                    | None -> ""
            sb.AppendFormat("  {0}: {1} — {2}{3}\n", idea.Id, idea.Name, oneLine idea.Goal, status) |> ignore
    sb.ToString()

/// The lesson request for one idea, after Ask's system prompt. `concepts` are what the learner knows.
let lessonPrompt (script: Script) (plan: Plan) (progress: Progress) (concepts: Map<string, Concept>) (idea: Idea) =
    let kind =
        if idea.Background then "background: the paper relies on it without explaining it. Teach it from standard knowledge, then say in a sentence how this paper uses it."
        elif idea.Section > 0 && idea.Section < script.Sections.Length then sprintf "from the paper, section \"%s\"" script.Sections.[idea.Section].Title
        else "from the paper"
    let moment =
        if idea.Background then
            "The learner is about to hear the next section of the paper read aloud, and needs this first. Prepare them for it."
        elif idea.Section > 0 && idea.Section < script.Sections.Length then
            sprintf "The learner has just heard the paper's section \"%s\" read aloud. Don't retell it: make sure they understood it. Explain what it means and why, clear up what is easy to miss, and connect it to what they know." script.Sections.[idea.Section].Title
        else "The learner has just heard the part of the paper about it read aloud. Don't retell it: make sure they understood it."
    let builds =
        idea.Requires
        |> List.choose plan.Idea
        |> List.map (fun i -> sprintf "%s \"%s\"" i.Id i.Name)
        |> fun xs -> xs @ (idea.Uses |> List.choose concepts.TryFind |> List.map (fun c -> sprintf "\"%s\" (the learner knows it from another paper: remind them in a phrase, don't teach it)" c.Name))
        |> function
            | [] -> "nothing earlier in the plan"
            | xs -> String.Join(", ", xs)
    let visuals =
        idea.Visuals
        |> List.choose script.Visual
        |> List.map (fun v -> sprintf "%s (%s)" v.Id (Help.visualName v))
        |> function
            | [] -> "none planned; pick one from VISUALS if it helps"
            | xs -> String.Join(", ", xs)
    taskHeader "You are the tutor in a study session: teach the learner one idea of the paper, then write questions that check they understood it."
    + $$"""THE STUDY PLAN (the learner goes through it in order)
{{planBlock plan progress idea.Id}}
TEACH NOW: {{idea.Id}} "{{idea.Name}}"
Goal: {{oneLine idea.Goal}}
Kind: {{kind}}
Builds on: {{builds}}
Equations, figures and tables about it: {{visuals}}

HOW TO TEACH
- The lesson is spoken to the learner by text-to-speech; they are listening, perhaps walking, with the equation, figure
  or table you SHOW on their screen. {{moment}}
- Write for this learner (THE LISTENER). They have been through the ideas before this one: build on them by name
  instead of explaining them again. Don't teach ideas that come later in the plan.
- Explain what it is, how it works, and why it is done this way (what problem it solves, what would go wrong without
  it). For an equation: what it computes, what its main symbols stand for, and why it has this form. For a result: what
  was compared, the key numbers, and what they show. For a limitation: what it is and why it matters.
- Write for the ear: 80 to 160 words, short sentences, conversational, like a good tutor talking to one student. No
  headings, lists, tables or parentheses. Bold (**...**) each key term where it is introduced.
- Math is read out, so keep formulas out of the sentences: say in words what a symbol stands for, and point to the
  formula on screen ("the formula on your screen"). Name a symbol as inline LaTeX ($d_k$) only when you must; never
  write display equations.
- Then one concrete example that makes it click, 30 to 90 words: a small worked example with round numbers, a case from
  the paper, or an everyday analogy (say it is an analogy, and where it breaks down if that matters).
- Stay faithful to the paper: its notation, terms and numbers. Never present as the paper's anything it doesn't say,
  and don't make up details it doesn't give. Background comes from standard textbook knowledge.

HOW TO WRITE THE QUESTIONS
- 3 multiple-choice questions about this idea, each from a different angle: why it is done this way, what would happen
  if something changed, applying it to a new case, what a symbol, term or result means, how it compares with an
  alternative. They test understanding: a learner who only memorized the wording should not be able to answer.
- The questions are read aloud with their options and answered by saying a letter: each question under 30 words, each
  option under 15 words.
- 3 options each, exactly one right. The right one follows from the paper (or standard knowledge, for background);
  check it before writing it. Each wrong option is a mistake a learner could really make: a common misconception, a
  near miss, a mix-up with a related idea, something that sounds right but isn't. Never silly, never a trick. All
  options alike in length, detail and style, so the form doesn't give the answer away: the right option must not be
  the longest or the only precise one (write the wrong ones with the same care, or shorten the right one). No "all of
  the above", "none of the above", or "which is NOT".
- For each option, one short sentence to the learner, read aloud after they answer: why it is right, or the
  misconception that makes it wrong.
- Each question must make sense on its own months from now, mixed with questions from other papers: name the method or
  paper ("In the Transformer, ..."), never "the paper", "the authors" or "the equation above". WITH shows one equation,
  figure or table with the question when the question is about it.
- Then 1 recall question, to answer from memory in a sentence or two, and its answer: the heart of the idea, precise
  enough to have one right answer.
- Math as inline LaTeX between single dollars ($\sqrt{d_k}$), sparingly.

FORMAT (plain text in exactly this shape, nothing before or after)
SHOW: <the id of the one equation, figure or table from VISUALS to show with the explanation, or none>
EXPLAIN:
<the explanation>
EXAMPLE:
<the example>
QUESTION: <question>
WITH: <id or none>
A: <option>
B: <option>
C: <option>
RIGHT: <letter>
WHY A: <one sentence>
WHY B: <one sentence>
WHY C: <one sentence>
QUESTION: <second question, in the same shape>
...
QUESTION: <third question, in the same shape>
...
RECALL: <question>
ANSWER: <answer>
"""

/// What the tutor has in front of it when the learner asks something in a study session.
type Moment =
    { /// The idea being studied: its name ("" between ideas, while the paper is read), what it is about, and where
      /// the paper covers it.
      Name: string
      Goal: string
      Background: bool
      Section: int
      /// The idea's lesson, for when the tutor hasn't said anything yet in this session.
      Lesson: Lesson option
      /// A question just answered, and the option chosen (None: "I don't know").
      Answered: (Question * int option) option
      /// What the tutor said last, oldest first: the lesson, a question and its options, the feedback on an answer.
      Said: string list
      /// The paper's sentences heard last, when the paper is being read.
      Heard: string list }

type TutorTurn =
    { Question: string
      Answer: string
      /// An equation, figure or table the answer points to, shown with it.
      Show: string option
      Followups: string list }

let private questionBlock (q: Question) (choice: int option) =
    let sb = StringBuilder()
    sb.AppendFormat("They were asked: \"{0}\"\n", oneLine q.Prompt) |> ignore
    for i, o in List.indexed q.Options do
        sb.AppendFormat("  {0}) {1}{2}\n", i + 1, oneLine o, (if i = q.Correct then "   (right)" else "")) |> ignore
    match choice with
    | Some c when c = q.Correct -> sb.Append("They answered right.\n") |> ignore
    | Some c when c >= 0 && c < q.Options.Length -> sb.AppendFormat("They chose option {0}, which is wrong.\n", c + 1) |> ignore
    | _ -> sb.Append("They said they didn't know.\n") |> ignore
    sb.ToString()

/// The latest question to the tutor, after Ask's system prompt and the conversation so far (see `tutorMessages`);
/// the reply follows its REPLY FORMAT.
let tutorPrompt (script: Script) (m: Moment) (question: string) =
    let sb = StringBuilder()
    sb.Append("STUDYING NOW: the learner is in a study session on this paper: they hear it read aloud section by section, \
               and between sections a tutor's spoken lessons and questions. Answer as that tutor (the HOW TO ANSWER rules \
               apply, about the moment below in place of LISTENING NOW). The messages before this one are your \
               conversation with the learner: build on them and on what the tutor just said aloud. Never repeat an \
               explanation already given: if they ask about it again, explain it another way or go deeper.\n\n") |> ignore
    if m.Name <> "" then
        sb.AppendFormat("Idea being studied: \"{0}\": {1}\n", m.Name, oneLine m.Goal) |> ignore
        if m.Background then sb.Append("Background knowledge the paper relies on (not explained in it).\n") |> ignore
        elif m.Section > 0 && m.Section < script.Sections.Length then
            sb.AppendFormat("In the paper: section \"{0}\".\n", script.Sections.[m.Section].Title) |> ignore
    if not m.Heard.IsEmpty then
        sb.Append("The paper is being read to them; they just heard:\n") |> ignore
        for x in m.Heard do sb.AppendFormat("  {0}\n", x) |> ignore
    match m.Lesson with
    | Some l when l.Explanation <> "" && m.Said.IsEmpty ->
        sb.Append("The lesson they heard:\n\"\"\"\n").Append(l.Explanation) |> ignore
        if l.Example <> "" then sb.Append("\n\nExample: ").Append(l.Example) |> ignore
        sb.Append("\n\"\"\"\n") |> ignore
    | _ -> ()
    match m.Answered with
    | Some (q, choice) -> sb.Append(questionBlock q choice) |> ignore
    | None -> ()
    if not m.Said.IsEmpty then
        sb.Append("\nWHAT THE TUTOR JUST SAID ALOUD (oldest first; \"this\", \"that\" and \"the answer\" usually mean the last of it):\n") |> ignore
        for x in m.Said do sb.Append("\"\"\"\n").Append(x.Trim()).Append("\n\"\"\"\n") |> ignore
    sb.Append('\n').Append(Help.showsVisuals).Append("QUESTION: ").Append(question) |> ignore
    sb.ToString()

/// The whole request: the paper, the conversation (the latest turns, as turns), and the question.
let tutorMessages (settings: Settings) (script: Script) (k: Help.Knowledge) (m: Moment) (history: TutorTurn list) (question: string) =
    [ yield "system", Help.systemPrompt settings script k
      for t in history |> List.rev |> List.truncate 6 |> List.rev do
          yield "user", t.Question
          // with the reply's tail, so the model keeps writing it
          yield "assistant", sprintf "%s\n---\nSHOW: %s\nNEXT: %s" t.Answer (defaultArg t.Show "none") (String.Join(" | ", t.Followups))
      yield "user", tutorPrompt script m question ]

/// The one-tap questions to the tutor: about the lesson, or about a question answered wrong.
let tutorTaps (answeredWrong: bool) =
    if answeredWrong then [ "Explain what I got wrong"; "Why is the right answer right?"; "Give me an example" ]
    else [ "Explain it more simply"; "Give me another example"; "Why is it done this way?" ]

/// Feedback on a recap answer, after Ask's system prompt.
let recapPrompt (plan: Plan) (partIndex: int) (answer: string) =
    let part = plan.Parts.[partIndex]
    let names = part.Ideas |> List.map (fun i -> i.Name) |> String.concat "; "
    taskHeader "Give the learner feedback on an answer they gave."
    + $$"""In a study session on this paper, the learner has just gone through these ideas: {{names}}.
They were asked to explain, from memory and in their own words:
"{{oneLine part.Recap}}"
A good answer covers (as the paper supports it): {{oneLine part.Points}}

Their answer (usually spoken aloud and transcribed automatically: ignore filler words, false starts and words the
transcription got wrong):
"""
    + "\"\"\"\n" + answer.Trim() + "\n\"\"\"\n\n"
    + """Judge understanding, not wording, spelling or style; a short answer that has the key ideas right has got it.
Talk to the learner in 2 to 5 short sentences, which are read aloud to them: first what they got right (specifically),
then what is missing or wrong, with the correct idea as the paper puts it. Encouraging and honest; don't repeat their
answer back, don't lecture beyond the question. No formulas; inline LaTeX only for a symbol you must name. Never invent
anything the paper doesn't say.

FORMAT
VERDICT: <got it, partly, or not yet>
<the feedback>
"""

/// Asks, for each idea of a plan said to be the same as one the learner knows, whether it really is: only confirmed
/// ones are skipped, since skipping an idea the learner doesn't know is worse than checking one they do.
let verifyPrompt (title: string) (pairs: (Idea * Concept) list) =
    let sb = StringBuilder()
    sb.AppendFormat("A learner is about to study \"{0}\". For each idea of its study plan below, an idea the learner already \
                     knows was proposed as the same idea, so that the tutor would skip teaching it. Check each proposal. Say \
                     yes only if someone who knows the known idea already knows the plan idea completely, as this paper uses \
                     it: the same concept, perhaps under another name. Say no if the plan idea uses, builds on, applies, \
                     extends, is part of or is an example of the known idea, or if the paper adds something important to \
                     it.\n\n", title) |> ignore
    for idea, c in pairs do
        sb.AppendFormat("{0} \"{1}\": {2}\n   proposed: \"{3}\": {4}\n\n", idea.Id, oneLine idea.Name, oneLine idea.Definition, oneLine c.Name, oneLine c.Definition) |> ignore
    sb.Append("Reply with one line per idea, like \"c7: no\", and nothing else.") |> ignore
    sb.ToString()

let parseVerdicts (text: string) : Map<string, bool> =
    [ for m in Regex.Matches(text, @"\b(c\w+)\s*[:=-]+\s*\**(yes|no)\b", RegexOptions.IgnoreCase) ->
          m.Groups.[1].Value.ToLowerInvariant(), m.Groups.[2].Value.ToLowerInvariant() = "yes" ]
    |> Map.ofList

/// Asks which ideas of a plan the learner has learned since it was made (in other papers).
let matchPrompt (title: string) (ideas: Idea list) (known: (string * Concept) list) =
    let sb = StringBuilder()
    sb.AppendFormat("Below are ideas from a study plan of \"{0}\", then ideas a learner has learned since from other papers. \
                     Which plan ideas are exactly the same idea as a learned one: the same concept, not merely related, \
                     not a special case or an extension?\n\nPLAN\n", title) |> ignore
    for i in ideas do sb.AppendFormat("{0}: {1} — {2}\n", i.Id, oneLine i.Name, oneLine i.Definition) |> ignore
    sb.Append("\nLEARNED\n").Append(knownBlock known) |> ignore
    sb.Append("\nReply with one line per match, like \"c3 = k1\", and nothing else. If none match, reply \"none\".") |> ignore
    sb.ToString()

let parseMatches (text: string) : (string * string) list =
    [ for m in Regex.Matches(text, @"\b(c\w+)\s*(?:=|->|:)\s*(k\d+)\b", RegexOptions.IgnoreCase) ->
          m.Groups.[1].Value.ToLowerInvariant(), m.Groups.[2].Value.ToLowerInvariant() ]

/// The voice the tutor speaks with, when none is chosen yet: another of the preset voices in the narrator's language,
/// another speaker if there is one, so the tutor never sounds like the paper.
let tutorVoice (narrator: string) (voices: Mistral.Voice list) : Mistral.Voice option =
    let parts (id: string) = id.Split('_')
    let language (id: string) = (parts id).[0]
    let speaker (id: string) = let p = parts id in if p.Length > 1 then p.[1] else id
    let others = voices |> List.filter (fun v -> v.Id <> narrator && language v.Id = language narrator)
    others
    |> List.tryFind (fun v -> speaker v.Id <> speaker narrator && v.Id.EndsWith "_neutral")
    |> Option.orElse (others |> List.tryFind (fun v -> speaker v.Id <> speaker narrator))
    |> Option.orElse (List.tryHead others)

// ---------------------------------------------------------------------------------------------
// Asking the model
// ---------------------------------------------------------------------------------------------

let private effort (settings: Settings) (e: string) = if settings.HelpModel.Contains "glm" then Some e else None

let private withPaper (settings: Settings) (script: Script) (k: Help.Knowledge) (user: string) =
    [ "system", Help.systemPrompt settings script k; "user", user ]

/// The ideas confirmed to be the same as the learner's (idea id -> Concept id), of the proposed ones.
let private confirm (settings: Settings) (title: string) (pairs: (Idea * Concept) list) (ct: CancellationToken) : Task<Map<string, string>> =
    task {
        if pairs.IsEmpty then return Map.empty
        else
            let! text = Mistral.chatStream settings.MistralApiKey settings.HelpModel [ "user", verifyPrompt title pairs ] (effort settings "low") ignore ct
            let verdicts = parseVerdicts text
            return
                pairs
                |> List.filter (fun (i, _) -> verdicts.TryFind i.Id = Some true)
                |> List.map (fun (i, c) -> i.Id, c.Id)
                |> Map.ofList
    }

/// Writes the plan; `onText` gets it as it streams in. Returns the text to save: known ideas by their own ids, and
/// "same as known" only where a second look confirmed it.
let makePlan (settings: Settings) (script: Script) (k: Help.Knowledge) (concepts: Concept list) (onText: string -> unit) (ct: CancellationToken) : Task<string> =
    task {
        let known = aliases concepts
        let! text = Mistral.chatStream settings.MistralApiKey settings.HelpModel (withPaper settings script k (planPrompt script known)) (effort settings "high") onText ct
        let text = resolveKnown known text
        let plan = parsePlan script text
        if plan.Parts.IsEmpty then failwith "The model didn't send a study plan. Try again."
        let byId = Map.ofList [ for c in concepts -> c.Id, c ]
        let proposed = plan.Ideas |> List.choose (fun i -> i.Same |> Option.bind byId.TryFind |> Option.map (fun c -> i, c))
        let! confirmed = confirm settings script.Title proposed ct
        return keepSame (confirmed |> Map.keys |> Set.ofSeq) text
    }

/// Writes an idea's lesson; `onText` gets it as it streams in.
let makeLesson (settings: Settings) (script: Script) (k: Help.Knowledge) (plan: Plan) (progress: Progress) (concepts: Map<string, Concept>)
               (idea: Idea) (onText: string -> unit) (ct: CancellationToken) : Task<string> =
    task {
        let! text =
            Mistral.chatStream settings.MistralApiKey settings.HelpModel (withPaper settings script k (lessonPrompt script plan progress concepts idea))
                (effort settings "high") onText ct
        if (parseLesson script "" idea.Id text).Explanation = "" then failwith "The model didn't send the lesson. Try again."
        return text
    }

/// Asks the tutor. Quick taps are answered fast; typed or spoken questions get more thought.
let askTutor (settings: Settings) (script: Script) (k: Help.Knowledge) (m: Moment) (history: TutorTurn list) (question: string) (quick: bool)
             (onText: string -> unit) (ct: CancellationToken) : Task<Help.Reply> =
    task {
        let! text =
            Mistral.chatStream settings.MistralApiKey settings.HelpModel (tutorMessages settings script k m history question)
                (effort settings (if quick then "low" else "high")) (Help.visibleAnswer >> onText) ct
        let reply = Help.parseReply script text
        if reply.Answer = "" then failwith "The model sent an empty answer. Try again."
        return reply
    }

/// Feedback on a recap answer; `onText` gets it as it streams in.
let feedback (settings: Settings) (script: Script) (k: Help.Knowledge) (plan: Plan) (partIndex: int) (answer: string)
             (onText: Feedback -> unit) (ct: CancellationToken) : Task<Feedback> =
    task {
        let! text =
            Mistral.chatStream settings.MistralApiKey settings.HelpModel (withPaper settings script k (recapPrompt plan partIndex answer))
                (effort settings "high") (parseFeedback >> onText) ct
        let f = parseFeedback text
        if f.Text = "" then failwith "The model sent no feedback. Try again."
        return f
    }

/// Ideas of the plan (not done yet, not linked) that are the same as ideas learned since `since`: idea -> Concept id.
let matchLearned (settings: Settings) (title: string) (plan: Plan) (progress: Progress) (concepts: Concept list) (ct: CancellationToken) : Task<Map<string, string>> =
    task {
        let ideas = plan.Ideas |> List.filter (fun i -> not (progress.Done.ContainsKey i.Id) && not (progress.Links.ContainsKey i.Id))
        let learned = concepts |> List.filter (fun c -> c.CreatedUtc > progress.Matched && c.Memory.Stage <> CardStage.New)
        if ideas.IsEmpty || learned.IsEmpty then return Map.empty
        else
            let known = learned |> List.mapi (fun i c -> sprintf "k%d" (i + 1), c)
            let! text = Mistral.chatStream settings.MistralApiKey settings.HelpModel [ "user", matchPrompt title ideas known ] (effort settings "low") ignore ct
            let byAlias = Map.ofList known
            let byIdea = Map.ofList [ for i in ideas -> i.Id, i ]
            let proposed =
                parseMatches text
                |> List.choose (fun (i, a) -> match byIdea.TryFind i, byAlias.TryFind a with | Some idea, Some c -> Some(idea, c) | _ -> None)
                |> List.distinctBy (fun (i, _) -> i.Id)
            return! confirm settings title proposed ct
    }

// ---------------------------------------------------------------------------------------------
// Saving
// ---------------------------------------------------------------------------------------------

let private outcomeName =
    function
    | Outcome.Learned -> "learned"
    | Outcome.TestedOut -> "tested"
    | Outcome.Known -> "known"
    | Outcome.Refreshed -> "refreshed"

let private outcomeOf =
    function
    | "tested" -> Outcome.TestedOut
    | "known" -> Outcome.Known
    | "refreshed" -> Outcome.Refreshed
    | _ -> Outcome.Learned

let private writeText (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path + ".tmp", text)
    File.Move(path + ".tmp", path, true)

/// Saves a new plan. Lessons are kept by the idea's id in the plan, so those of an earlier plan go.
let savePlan (p: Store.Paths) (id: string) (text: string) =
    if Directory.Exists(p.StudyDir id) then
        for f in Directory.GetFiles(p.StudyDir id, "lesson-*.txt") do
            try File.Delete f with _ -> ()
    writeText (p.StudyPlan id) text

let loadPlan (p: Store.Paths) (id: string) (script: Script) : Plan option =
    try
        let plan = parsePlan script (File.ReadAllText(p.StudyPlan id))
        if plan.Parts.IsEmpty then None else Some plan
    with _ -> None

let saveLesson (p: Store.Paths) (id: string) (idea: string) (text: string) = writeText (p.StudyLesson(id, idea)) text

let loadLesson (p: Store.Paths) (id: string) (script: Script) (idea: string) : Lesson option =
    try
        let path = p.StudyLesson(id, idea)
        if File.Exists path then Some(parseLesson script id idea (File.ReadAllText path)) else None
    with _ -> None

let saveProgress (p: Store.Paths) (id: string) (s: Progress) =
    let path = p.StudyProgress id
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    do
        use fs = File.Create(path + ".tmp")
        use w = new Utf8JsonWriter(fs)
        w.WriteStartObject()
        w.WriteStartObject "links"
        for KeyValue (k, v) in s.Links do w.WriteString(k, v)
        w.WriteEndObject()
        w.WriteStartObject "done"
        for KeyValue (k, v) in s.Done do w.WriteString(k, outcomeName v)
        w.WriteEndObject()
        let list (name: string) (xs: string seq) =
            w.WriteStartArray name
            for x in xs do w.WriteStringValue x
            w.WriteEndArray()
        w.WriteStartArray "recaps"
        for r in s.Recaps do w.WriteNumberValue r
        w.WriteEndArray()
        list "teach" s.Teach
        list "reported" s.Reported
        w.WriteString("matched", s.Matched.ToString("o"))
        w.WriteNumber("heard", s.Heard)
        w.WriteString("at", s.At)
        w.WriteEndObject()
        w.Flush()
    File.Move(path + ".tmp", path, true)

let loadProgress (p: Store.Paths) (id: string) : Progress option =
    try
        use d = JsonDocument.Parse(File.ReadAllText(p.StudyProgress id))
        let e = d.RootElement
        let map (name: string) (f: string -> 'a) =
            match e.TryGetProperty name with
            | true, o when o.ValueKind = JsonValueKind.Object -> o.EnumerateObject() |> Seq.map (fun x -> x.Name, f (x.Value.GetString())) |> Map.ofSeq
            | _ -> Map.empty
        let strings (name: string) =
            match e.TryGetProperty name with
            | true, a when a.ValueKind = JsonValueKind.Array -> a.EnumerateArray() |> Seq.map (fun x -> x.GetString()) |> Set.ofSeq
            | _ -> Set.empty
        Some
            { Links = map "links" (fun x -> x)
              Done = map "done" outcomeOf
              Recaps =
                match e.TryGetProperty "recaps" with
                | true, a when a.ValueKind = JsonValueKind.Array -> a.EnumerateArray() |> Seq.map (fun x -> x.GetInt32()) |> Set.ofSeq
                | _ -> Set.empty
              Teach = strings "teach"
              Reported = strings "reported"
              Matched =
                match e.TryGetProperty "matched" with
                | true, t when t.ValueKind = JsonValueKind.String ->
                    match DateTime.TryParse(t.GetString(), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
                    | true, t -> t.ToUniversalTime()
                    | _ -> DateTime.UtcNow
                | _ -> DateTime.UtcNow
              Heard =
                match e.TryGetProperty "heard" with
                | true, h when h.ValueKind = JsonValueKind.Number -> h.GetInt32()
                | _ -> 0
              At =
                match e.TryGetProperty "at" with
                | true, a when a.ValueKind = JsonValueKind.String -> a.GetString()
                | _ -> "" }
    with _ -> None
