/// Studying a paper with a tutor. The model (with Ask's system prompt: the whole paper, every equation, figure and
/// table) plans the ideas to learn, teaches them one at a time and writes questions that check them; what the learner
/// knows is kept across papers (Knowledge), so a new paper skips the ideas already known, or checks them with one
/// quick question when they are fading.
///
/// It follows what is known to make learning last rather than to feel easy:
/// - small steps, prerequisites first, background only where this learner needs it (segmenting, pre-training);
/// - each explanation tied to the equation or figure on screen, with a concrete example (dual coding, worked examples);
/// - retrieval right after: multiple-choice questions whose wrong options are real misconceptions, feedback on every
///   option, and "I don't know" rather than a guess (retrieval practice, feedback);
/// - when an answer is wrong, the tutor explains what was missed, then a different question on the same idea (mastery);
/// - the idea comes back minutes later in the session, between other ideas, then at growing intervals (spacing,
///   interleaving, FSRS), each time with a different question;
/// - at the end of each part, the learner explains its ideas in their own words and gets feedback against the paper
///   (generation, elaboration);
/// - known ideas are tested, never assumed: "I know this" asks the question first.
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
      Matched: DateTime }

module Progress =
    let empty (now: DateTime) =
        { Links = Map.empty; Done = Map.empty; Recaps = Set.empty; Teach = Set.empty; Reported = Set.empty; Matched = now }

/// What to do next in a study session.
[<RequireQualifiedAccess>]
type Step =
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

/// The next step, and the progress with the known ideas passed on the way marked as skipped. `last` is the idea
/// (Concept id) just asked about, so it isn't asked again straight away.
let next (now: DateTime) (plan: Plan) (progress: Progress) (concepts: Map<string, Concept>) (last: string option) : Step * Progress =
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
        let mutable p = progress
        let mutable step = None
        for pi, part in List.indexed plan.Parts do
            if step.IsNone then
                for idea in part.Ideas do
                    if step.IsNone && not (p.Done.ContainsKey idea.Id) then
                        if p.Teach.Contains idea.Id then step <- Some(Step.Teach idea.Id)
                        else
                            match p.Links.TryFind idea.Id |> Option.bind concepts.TryFind with
                            | Some c when Knowledge.skippable now c -> p <- { p with Done = p.Done.Add(idea.Id, Outcome.Known) }
                            | Some c when c.Memory.Stage <> CardStage.New -> step <- Some(Step.Check(idea.Id, c.Id))
                            | _ -> step <- Some(Step.Teach idea.Id)
                // a part whose ideas were all known already needs no recap
                let learnedHere = part.Ideas |> List.exists (fun i -> p.Done.TryFind i.Id |> Option.exists (fun o -> o <> Outcome.Known))
                if step.IsNone && part.Recap <> "" && learnedHere && not (p.Recaps.Contains pi) then step <- Some(Step.Recap pi)
        defaultArg step Step.Finished, p

/// The ideas to be taught next, in order, for writing their lessons ahead. Known ideas aren't among them: they are
/// only taught if a quick question shows they are forgotten.
let upcoming (plan: Plan) (progress: Progress) (concepts: Map<string, Concept>) (count: int) =
    plan.Ideas
    |> List.filter (fun i ->
        not (progress.Done.ContainsKey i.Id)
        && (progress.Teach.Contains i.Id
            || (match progress.Links.TryFind i.Id |> Option.bind concepts.TryFind with
                | Some c -> c.Memory.Stage = CardStage.New
                | None -> true)))
    |> List.truncate count
    |> List.map (fun i -> i.Id)

/// Roughly how long what is left takes: a few minutes per idea to learn, one per quick check, two per recap.
let minutesLeft (now: DateTime) (plan: Plan) (progress: Progress) (concepts: Map<string, Concept>) =
    let ideas =
        plan.Ideas
        |> List.filter (fun i -> not (progress.Done.ContainsKey i.Id))
        |> List.sumBy (fun i ->
            match progress.Links.TryFind i.Id |> Option.bind concepts.TryFind with
            | _ when progress.Teach.Contains i.Id -> 3
            | Some c when Knowledge.skippable now c -> 0
            | Some c when c.Memory.Stage <> CardStage.New -> 1
            | _ -> 3)
    let recaps = plan.Parts |> List.indexed |> List.filter (fun (i, p) -> p.Recap <> "" && not (progress.Recaps.Contains i)) |> List.length
    ideas + 2 * recaps

/// Where the narration covers an idea: where its first equation or figure is read, or where its section starts.
let segmentOf (script: Script) (idea: Idea) : int option =
    let order = Narration.equationOrder script
    idea.Visuals
    |> List.tryPick (fun v -> order |> Array.tryFind (fun (x, _) -> x.Id = v) |> Option.map snd)
    |> Option.orElse (if idea.Section > 0 && idea.Section < script.Sections.Length then Some script.Sections.[idea.Section].FirstSegment else None)

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
    + $$"""HOW TO PLAN
- List the ideas a learner must understand to really know this paper. For a research paper: the problem and why it
  matters, the key idea and how it differs from earlier work, each part of the method (its important equations: what
  they compute and why they have that form), the main results and what they show, and the limitations. For any other
  document (a book, an article, a report, slides, notes): its main ideas, arguments, terms, and key examples.
- Add background ideas the paper relies on without explaining them, only when this learner may not know them (see THE
  LISTENER and WHAT THE LEARNER ALREADY KNOWS), each just before the first idea that needs it.
- One idea each: small enough to explain in about 150 words and to check with one question. Split bigger ones.
- Teaching order: every idea after the ones it builds on; otherwise the paper's order.
- Usually 8 to 20 ideas; up to 30 for a long or dense document. Prefer what an expert would still want to know a year
  from now. Leave out related work, setup trivia and acknowledgements.
- Group the ideas into 2 to 6 parts that follow the paper's arc. At the end of each part: a question the learner
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
section: <the number from SECTIONS where the paper covers it, or none>
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
- Write for this learner (THE LISTENER). They have been through the ideas before this one: build on them by name
  instead of explaining them again. Don't teach ideas that come later in the plan.
- Explain what it is, how it works, and why it is done this way (what problem it solves, what would go wrong without
  it). For an equation: what it computes, what each symbol stands for, and why it has this form. For a result: what
  was compared, the key numbers, and what they show. For a limitation: what it is and why it matters.
- 100 to 220 words in short paragraphs, conversational, like a good tutor talking to one student. No headings; a list
  only for steps. Bold (**...**) each key term where it is introduced.
- The equation, figure or table you SHOW is on the learner's screen next to your text: talk them through it.
- Then one concrete example that makes it click, 40 to 120 words: a small worked example with numbers, a case from the
  paper, or an everyday analogy (say it is an analogy, and where it breaks down if that matters).
- Stay faithful to the paper: its notation, terms and numbers. Never present as the paper's anything it doesn't say,
  and don't make up details it doesn't give. Background comes from standard textbook knowledge.

HOW TO WRITE THE QUESTIONS
- 3 multiple-choice questions about this idea, each from a different angle: why it is done this way, what would happen
  if something changed, applying it to a new case, what a symbol, term or result means, how it compares with an
  alternative. They test understanding: a learner who only memorized the wording should not be able to answer.
- 4 options each, exactly one right. The right one follows from the paper (or standard knowledge, for background);
  check it before writing it. Each wrong option is a mistake a learner could really make: a common misconception, a
  near miss, a mix-up with a related idea, something that sounds right but isn't. Never silly, never a trick. All
  options alike in length, detail and style, so the form doesn't give the answer away: the right option must not be
  the longest or the only precise one (write the wrong ones with the same care, or shorten the right one). No "all of
  the above", "none of the above", or "which is NOT".
- For each option, one sentence to the learner: why it is right, or the misconception that makes it wrong.
- Each question must make sense on its own months from now, mixed with questions from other papers: name the method or
  paper ("In the Transformer, ..."), never "the paper", "the authors" or "the equation above". WITH shows one equation,
  figure or table with the question when the question is about it.
- Then 1 recall question, to answer from memory in a sentence or two, and its answer: the heart of the idea, precise
  enough to have one right answer.
- Math as inline LaTeX between single dollars ($\sqrt{d_k}$) everywhere; in the explanation a formula that matters may
  stand alone on its own line as $$...$$.

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
D: <option>
RIGHT: <letter>
WHY A: <one sentence>
WHY B: <one sentence>
WHY C: <one sentence>
WHY D: <one sentence>
QUESTION: <second question, in the same shape>
...
QUESTION: <third question, in the same shape>
...
RECALL: <question>
ANSWER: <answer>
"""

/// What the tutor has in front of it when the learner asks something in a study session.
type Moment =
    { /// The idea being studied: its name, what it is about, and where the paper covers it.
      Name: string
      Goal: string
      Background: bool
      Section: int
      /// The lesson the learner read, if any.
      Lesson: Lesson option
      /// A question just answered, and the option chosen (None: "I don't know").
      Answered: (Question * int option) option }

type TutorTurn = { Question: string; Answer: string }

let private questionBlock (q: Question) (choice: int option) =
    let sb = StringBuilder()
    sb.AppendFormat("They were asked: \"{0}\"\n", oneLine q.Prompt) |> ignore
    for i, o in List.indexed q.Options do
        sb.AppendFormat("  {0}) {1}{2}\n", letters.[i], oneLine o, (if i = q.Correct then "   (right)" else "")) |> ignore
    match choice with
    | Some c when c = q.Correct -> sb.Append("They answered right.\n") |> ignore
    | Some c when c >= 0 && c < q.Options.Length -> sb.AppendFormat("They chose {0}), which is wrong.\n", letters.[c]) |> ignore
    | _ -> sb.Append("They said they didn't know.\n") |> ignore
    sb.ToString()

/// A question to the tutor, after Ask's system prompt; the reply follows its REPLY FORMAT.
let tutorPrompt (script: Script) (m: Moment) (history: TutorTurn list) (question: string) =
    let sb = StringBuilder()
    sb.Append("STUDYING NOW: the learner is in a study session on this paper, reading a tutor's lessons and answering its \
               questions instead of listening. Answer as that tutor, about the idea below (the HOW TO ANSWER rules apply, \
               about this idea in place of LISTENING NOW). Build on the lesson they read rather than repeating it.\n\n") |> ignore
    sb.AppendFormat("Idea: \"{0}\": {1}\n", m.Name, oneLine m.Goal) |> ignore
    if m.Background then sb.Append("Background knowledge the paper relies on (not explained in it).\n") |> ignore
    elif m.Section > 0 && m.Section < script.Sections.Length then
        sb.AppendFormat("In the paper: section \"{0}\".\n", script.Sections.[m.Section].Title) |> ignore
    match m.Lesson with
    | Some l when l.Explanation <> "" ->
        sb.Append("The lesson they read:\n\"\"\"\n").Append(l.Explanation) |> ignore
        if l.Example <> "" then sb.Append("\n\nExample: ").Append(l.Example) |> ignore
        sb.Append("\n\"\"\"\n") |> ignore
    | _ -> ()
    match m.Answered with
    | Some (q, choice) -> sb.Append(questionBlock q choice) |> ignore
    | None -> ()
    if not history.IsEmpty then
        sb.Append("\nEarlier in this session, about this idea:\n") |> ignore
        for t in history |> List.rev |> List.truncate 4 |> List.rev do
            sb.AppendFormat("Q: {0}\nA: {1}\n", t.Question, t.Answer) |> ignore
    sb.Append("\nQUESTION: ").Append(question) |> ignore
    sb.ToString()

/// The one-tap questions to the tutor: about the lesson, or about a question answered wrong.
let tutorTaps (answeredWrong: bool) =
    if answeredWrong then [ "Explain what I got wrong"; "Why is the right answer right?"; "Give me an example" ]
    else [ "Explain it more simply"; "Give me another example"; "Why is it done this way?" ]

/// Feedback on a recap answer, after Ask's system prompt.
let recapPrompt (plan: Plan) (partIndex: int) (answer: string) =
    let part = plan.Parts.[partIndex]
    let names = part.Ideas |> List.map (fun i -> i.Name) |> String.concat "; "
    taskHeader "Give the learner feedback on an answer they wrote."
    + $$"""In a study session on this paper, the learner has just gone through these ideas: {{names}}.
They were asked to explain, from memory and in their own words:
"{{oneLine part.Recap}}"
A good answer covers (as the paper supports it): {{oneLine part.Points}}

Their answer:
"""
    + "\"\"\"\n" + answer.Trim() + "\n\"\"\"\n\n"
    + """Judge understanding, not wording, spelling or style; a short answer that has the key ideas right has got it.
Talk to the learner in 2 to 5 sentences: first what they got right (specifically), then what is missing or wrong, with
the correct idea as the paper puts it. Encouraging and honest; don't repeat their answer back, don't lecture beyond the
question. Math as inline LaTeX between single dollars. Never invent anything the paper doesn't say.

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
            Mistral.chatStream settings.MistralApiKey settings.HelpModel (withPaper settings script k (tutorPrompt script m history question))
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
                | _ -> DateTime.UtcNow }
    with _ -> None
