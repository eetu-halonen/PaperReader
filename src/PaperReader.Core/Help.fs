/// Questions while listening ("Ask"): what the answering model is told, the one-tap questions, and its replies.
///
/// The model gets everything it needs to answer about *this* moment without the listener typing it:
/// - the whole paper as Mistral OCR read it (markdown, formulas in LaTeX, tables as tables), so answers are
///   grounded in the paper and "equation 3" or "table 2" mean what they mean in it;
/// - every equation, figure and table by id with its LaTeX or caption, and for figures a detailed description
///   made once by a vision model (the answering model can't see images);
/// - where the listener is: the section, the last sentences they heard (as narrated), the one playing, and
///   what is on screen, so "this" and "that" resolve;
/// - the earlier questions about this paper, and a line about the listener, so answers fit them.
module PaperReader.Core.Help

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// The one-tap questions, and a typed or spoken one.
[<RequireQualifiedAccess>]
type Ask =
    | Simpler
    | Example
    | WhyItMatters
    | Recap
    /// The equation, figure or table the question is about, explained piece by piece.
    | Walkthrough
    | TellMore
    | Define of term: string
    /// A follow-up the model offered: already specific, so answered quickly.
    | Followup of text: string
    | Free of text: string

let visualName (v: Visual) =
    match v.Kind, v.EqNumber with
    | VisualKind.Algorithm, Some n -> sprintf "Algorithm %s" n
    | VisualKind.Algorithm, None -> "Algorithm"
    | VisualKind.Equation, Some n when n.Contains "–" -> sprintf "Equations (%s)" n
    | VisualKind.Equation, Some n -> sprintf "Equation (%s)" n
    | VisualKind.Equation, None -> "Equation"
    | VisualKind.Figure, Some n -> sprintf "Figure %s" n
    | VisualKind.Figure, None -> "Figure"
    | VisualKind.Table, Some n -> sprintf "Table %s" n
    | VisualKind.Table, None -> "Table"
    | VisualKind.Inline, _ -> "the math in this sentence"

/// How a question names a visual: by its number, or "this equation" when the paper doesn't number it.
let spokenName (v: Visual) =
    match v.Kind, v.EqNumber with
    | VisualKind.Inline, _ -> "the math in this sentence"
    | _, Some _ -> visualName v
    | VisualKind.Equation, None -> "this equation"
    | VisualKind.Algorithm, None -> "this algorithm"
    | VisualKind.Figure, None -> "this figure"
    | VisualKind.Table, None -> "this table"

/// The question as the listener sees it in the conversation (and as it is sent).
let questionText (ask: Ask) (about: Visual option) =
    match ask, about with
    | Ask.Simpler, Some v when v.Kind <> VisualKind.Inline -> sprintf "I didn't get %s. Can you explain it more simply?" (spokenName v)
    | Ask.Simpler, _ -> "I didn't get that. Can you explain it more simply?"
    | Ask.Example, _ -> "Can you give me a concrete example?"
    | Ask.WhyItMatters, _ -> "Why does this matter?"
    | Ask.Recap, _ -> "Recap what I've heard so far."
    | Ask.Walkthrough, Some v ->
        match v.Kind with
        | VisualKind.Figure -> sprintf "What does %s show, and what's the takeaway?" (spokenName v)
        | VisualKind.Table -> sprintf "What does %s tell us?" (spokenName v)
        | VisualKind.Algorithm -> sprintf "Walk me through %s step by step." (spokenName v)
        | _ -> sprintf "Walk me through %s, piece by piece." (spokenName v)
    | Ask.Walkthrough, None -> "Walk me through that step by step."
    | Ask.TellMore, _ -> "Tell me more. Go deeper."
    | Ask.Define term, _ -> sprintf "What is %s?" term
    | Ask.Followup text, _ | Ask.Free text, _ -> text.Trim()

/// Quick taps get a fast answer; explanations that need care (and anything typed or spoken) get reasoning.
let effort (ask: Ask) =
    match ask with
    | Ask.Free _ | Ask.TellMore | Ask.Walkthrough -> "high"
    | _ -> "low"

// ---------------------------------------------------------------------------------------------
// What the model knows
// ---------------------------------------------------------------------------------------------

/// Per paper, prepared once (see `prepare`).
type Knowledge =
    { /// The paper as Mistral OCR read it, page by page.
      PaperText: string
      /// Figure id -> detailed description of the image.
      FigureNotes: Map<string, string> }

/// Joins OCR pages into one text; image placeholders are dropped (figures are described separately).
let paperText (pages: Mistral.OcrPage list) =
    let sb = StringBuilder()
    for p in pages |> List.sortBy (fun p -> p.Index) do
        sb.AppendFormat("\n\n<!-- page {0} -->\n\n", p.Index + 1) |> ignore
        sb.Append(Regex.Replace(p.Markdown, @"!\[[^\]]*\]\([^)]*\)", "")) |> ignore
    sb.ToString().Trim()

/// Papers are rarely this long; past it the tail (usually appendices) is left out to bound the cost.
let private maxPaperChars = 600_000

/// What the narration said while a visual was first on screen: its reading and explanation.
let private narrationOf (script: Script) (id: string) =
    let said =
        script.Segments
        |> Array.filter (fun s -> s.Show = Some id && s.Reason = ShowReason.Own)
        |> Array.map (fun s -> s.Say)
        |> String.concat " "
    if said.Length > 700 then said.Substring(0, 700) + "…" else said

let private oneLine (s: string) = Regex.Replace(s, @"\s+", " ").Trim()

let private visualsBlock (script: Script) (k: Knowledge) =
    let sb = StringBuilder()
    for v in script.Visuals do
        if v.Kind <> VisualKind.Inline then
            sb.AppendFormat("[{0}] {1}, page {2}.", v.Id, visualName v, v.Page + 1) |> ignore
            match v.Kind with
            | VisualKind.Figure | VisualKind.Table ->
                if v.RawText <> "" then sb.Append(" Caption: ").Append(oneLine v.RawText) |> ignore
                match k.FigureNotes.TryFind v.Id with
                | Some n -> sb.Append(" What the image shows: ").Append(oneLine n) |> ignore
                | None -> ()
            | _ ->
                match v.Latex with
                | Some l -> sb.Append(" LaTeX: ").Append(oneLine l) |> ignore
                | None -> sb.Append(" Text: ").Append(oneLine v.RawText) |> ignore
            let said = narrationOf script v.Id
            if said <> "" then sb.Append(" The narration said: ").Append(said) |> ignore
            sb.Append('\n') |> ignore
    sb.ToString()

/// The fixed part of the prompt: instructions and the paper. It is the same for every question about a paper,
/// so it comes first (the API caches a repeated prefix).
let systemPrompt (settings: Settings) (script: Script) (k: Knowledge) =
    let text = if k.PaperText.Length > maxPaperChars then k.PaperText.Substring(0, maxPaperChars) + "\n[… rest of the paper left out]" else k.PaperText
    let listener =
        if String.IsNullOrWhiteSpace settings.AboutMe then
            "Unknown. Assume a curious reader with undergraduate maths who may not know this field: define jargon briefly the first time."
        else settings.AboutMe.Trim()
    $$"""You are the study companion inside Paper Reader, an app that reads research papers aloud. The listener is
hearing a narrated version of the paper below. They cannot see its text, only the current equation, figure or
table image. They paused to ask you something about what they just heard.

HOW TO ANSWER
- Answer about the part they are listening to (LISTENING NOW), unless the question is clearly about something else.
- The paper is the source of truth. Use its notation and numbers, and name equations, figures, tables and sections
  as the paper numbers them. Never invent results or numbers.
- When you add background that is not in the paper, say so in a few words ("Outside this paper, ..."). If the paper
  doesn't say, say that, then give the best general answer.
- Keep it short and conversational, like a good tutor talking: 2 to 5 sentences (about 60 to 120 words). Go
  longer only when asked to go deeper or for a walkthrough (then up to about 250 words). No headings. At most one
  short list.
- Math: write every symbol with a subscript or superscript, and every short expression, as inline LaTeX between
  single dollars: $W_i^Q$, $d_k$, $\sqrt{d_k}$, $\mathbb{R}^{d \times k}$. Never write ^ or _ outside dollars.
  A longer formula that matters goes alone on its own line as $$LaTeX$$ (at most two such lines).
- Never write the bracketed ids (like [E3]) in the answer; they are only for SHOW. Say "the equation on
  screen" or use the paper's own numbering.
- For a recap, cover only what comes before the listener's position. Don't spoil what is ahead unless asked.
- Answer in the language of the question.

THE LISTENER
{{listener}}

REPLY FORMAT
Write the answer. Then a line with only "---", then two lines:
SHOW: <the id (like E3 or Fig2) of one equation, figure or table from VISUALS worth looking at with the answer, or none>
NEXT: <three follow-up questions this listener is likely to want next, each under 8 words, plain text without
LaTeX or dollars, separated by " | ">

PAPER: {{script.Title}}

VISUALS (id, name, page, content)
{{visualsBlock script k}}
FULL TEXT (Mistral OCR: markdown, formulas in LaTeX)
{{text}}
"""

let private sectionTitle (script: Script) (section: int) =
    if section > 0 && section < script.Sections.Length then script.Sections.[section].Title else "the beginning"

/// Where the listener is and what the question is about.
let userPrompt (script: Script) (position: int) (about: Visual option) (question: string) =
    let segs = script.Segments
    let position = max 0 (min position (segs.Length - 1))
    let seg = segs.[position]
    let sb = StringBuilder()
    sb.AppendFormat("LISTENING NOW: section \"{0}\", page {1}.\n", sectionTitle script seg.Section, seg.Page + 1) |> ignore
    sb.Append("Just heard:\n") |> ignore
    for i in max 0 (position - 8) .. position - 1 do
        if segs.[i].Say <> "" then sb.AppendFormat("  {0}\n", segs.[i].Say) |> ignore
    sb.AppendFormat("  >>> {0}   (playing now)\n", seg.Say) |> ignore
    let ahead = [ for i in position + 1 .. min (segs.Length - 1) (position + 2) -> segs.[i].Say ] |> List.filter ((<>) "")
    if not ahead.IsEmpty then sb.AppendFormat("Coming next (not heard yet): {0}\n", String.Join(" ", ahead)) |> ignore
    match seg.Show |> Option.bind script.Visual with
    | Some v when v.Kind <> VisualKind.Inline -> sb.AppendFormat("On screen: [{0}] {1}\n", v.Id, visualName v) |> ignore
    | _ -> ()
    match about with
    | Some v -> sb.AppendFormat("The question is about [{0}] {1}.\n", v.Id, visualName v) |> ignore
    | None -> sb.Append("The question is about what they just heard.\n") |> ignore
    sb.Append("\nQUESTION: ").Append(question) |> ignore
    sb.ToString()

/// The conversation for one question: earlier questions about the paper (the latest few) and this one.
let messages (settings: Settings) (script: Script) (k: Knowledge) (history: HelpTurn list) (position: int) (about: Visual option) (question: string) =
    [ yield "system", systemPrompt settings script k
      for t in history |> List.rev |> List.truncate 6 |> List.rev do
          let seg = script.Segments.[max 0 (min t.Segment (script.Segments.Length - 1))]
          yield "user", sprintf "(Asked in section \"%s\") %s" (sectionTitle script seg.Section) t.Question
          yield "assistant", sprintf "%s\n---\nSHOW: %s\nNEXT: %s" t.Answer (defaultArg t.Show "none") (String.Join(" | ", t.Followups))
      yield "user", userPrompt script position about question ]

// ---------------------------------------------------------------------------------------------
// Reading the reply
// ---------------------------------------------------------------------------------------------

type Reply = { Answer: string; Show: string option; Followups: string list }

/// The part of a reply being streamed that is answer text (the SHOW/NEXT tail is held back).
let visibleAnswer (partial: string) =
    let cut = partial.IndexOf "\n---"
    let text = if cut >= 0 then partial.Substring(0, cut) else partial
    // a separator just starting to arrive
    let text = Regex.Replace(text, @"\n-{0,2}$", "")
    text.Trim()

let parseReply (script: Script) (text: string) : Reply =
    let answer = visibleAnswer text
    let cut = text.IndexOf "\n---"
    let tail = if cut >= 0 then text.Substring(cut) else ""
    let field (name: string) =
        let m = Regex.Match(tail, "^\\s*\\**" + name + "\\**\\s*:\\s*(.+)$", RegexOptions.Multiline ||| RegexOptions.IgnoreCase)
        if m.Success then Some(m.Groups.[1].Value.Trim()) else None
    let show =
        field "SHOW"
        |> Option.map (fun s -> s.Trim('[', ']', '*', ' ', '.'))
        |> Option.filter (fun id -> script.Visual id |> Option.isSome)
    let next =
        match field "NEXT" with
        | Some n ->
            n.Split('|')
            |> Array.map (fun q -> q.Trim().Trim('"', '*', '-', ' '))
            |> Array.filter (fun q -> q <> "" && q.Length <= 80)
            |> Array.truncate 3
            |> List.ofArray
        | None -> []
    { Answer = answer; Show = show; Followups = next }

/// Answer text split into prose and display formulas ($$…$$), for showing formulas typeset.
[<RequireQualifiedAccess>]
type Piece =
    | Prose of string
    | Formula of latex: string

let pieces (answer: string) : Piece list =
    let parts = Regex.Split(answer, @"\$\$(.+?)\$\$", RegexOptions.Singleline)
    [ for i in 0 .. parts.Length - 1 do
          let p = parts.[i].Trim()
          if p <> "" then
              if i % 2 = 1 then yield Piece.Formula p
              else
                  // **bold** and inline $…$ math are drawn by the view
                  yield Piece.Prose p ]

// ---------------------------------------------------------------------------------------------
// Reading math aloud
// ---------------------------------------------------------------------------------------------

let private latexWords =
    dict [ "alpha", "alpha"; "beta", "beta"; "gamma", "gamma"; "delta", "delta"; "epsilon", "epsilon"; "varepsilon", "epsilon"
           "zeta", "zeta"; "eta", "eta"; "theta", "theta"; "iota", "iota"; "kappa", "kappa"; "lambda", "lambda"; "mu", "mu"
           "nu", "nu"; "xi", "xi"; "pi", "pi"; "rho", "rho"; "sigma", "sigma"; "tau", "tau"; "phi", "phi"; "varphi", "phi"
           "chi", "chi"; "psi", "psi"; "omega", "omega"; "Gamma", "Gamma"; "Delta", "Delta"; "Theta", "Theta"; "Lambda", "Lambda"
           "Sigma", "Sigma"; "Phi", "Phi"; "Psi", "Psi"; "Omega", "Omega"; "Pi", "Pi"
           "times", " times "; "cdot", " times "; "div", " divided by "; "pm", " plus or minus "; "leq", " at most "; "le", " at most "
           "geq", " at least "; "ge", " at least "; "neq", " not equal to "; "approx", " approximately "; "sim", " distributed as "
           "in", " in "; "to", " to "; "rightarrow", " to "; "infty", "infinity"; "partial", "partial "; "nabla", "gradient of "
           "sum", "the sum of "; "prod", "the product of "; "int", "the integral of "; "log", "log "; "exp", "exp "; "max", "max "
           "min", "min "; "softmax", "softmax "; "top", " transpose"; "ldots", " and so on "; "cdots", " and so on "; "dots", " and so on " ]

/// A LaTeX formula as it would be read aloud ("W sub i to the power Q", "the square root of d sub k").
let speakLatex (latex: string) =
    // innermost groups first, so nested fractions and roots read in order
    let mutable t = latex.Replace(@"\left", "").Replace(@"\right", "")
    let group = @"\{([^{}]*)\}"
    let power (x: string) =
        match x.Trim() with
        | "2" -> " squared "
        | "T" | @"\top" -> " transpose "
        | x -> " to the power " + x + " "
    let mutable changed = true
    while changed do
        let before = t
        t <- Regex.Replace(t, @"\\[dt]?frac\s*" + group + @"\s*" + group, " $1 over $2 ")
        t <- Regex.Replace(t, @"\\sqrt\s*" + group, " the square root of $1 ")
        t <- Regex.Replace(t, @"\\(?:mathbb|mathbf|mathrm|mathcal|text|textbf|operatorname|mathit|boldsymbol|hat|bar|tilde|vec)\s*" + group, "$1")
        t <- Regex.Replace(t, @"\^\s*" + group, fun m -> power m.Groups.[1].Value)
        t <- Regex.Replace(t, @"_\s*" + group, " sub $1 ")
        changed <- t <> before
    t <- Regex.Replace(t, @"\^\s*(\\?\w)", fun m -> power m.Groups.[1].Value)
    t <- Regex.Replace(t, @"_\s*(\\?\w)", " sub $1 ")
    t <- Regex.Replace(t, @"\\([A-Za-z]+)", fun m -> match latexWords.TryGetValue m.Groups.[1].Value with | true, w -> w | _ -> m.Groups.[1].Value)
    t <- t.Replace(@"\,", " ").Replace(@"\;", " ").Replace(@"\!", "")
    t <- t.Replace("{", "").Replace("}", "").Replace("=", " equals ").Replace("+", " plus ").Replace(@"\", " ")
    t <- Regex.Replace(t, @"(?<=\w|\))\s*-\s*(?=\w|\()", " minus ")
    // products of single-letter variables: "QK" is Q times K
    t <- Regex.Replace(t, @"\b[A-Z]{2,3}\b", fun m -> String.Join(" ", m.Value.ToCharArray()))
    Regex.Replace(t, @"\s+", " ").Trim()

/// The answer as it should be spoken: formulas left to the screen.
let spoken (answer: string) =
    pieces answer
    |> List.map (function
        | Piece.Prose p ->
            let p = Regex.Replace(p, @"(?m)^\s*[-*•]\s+", "")
            let p = Regex.Replace(p, @"\*\*(.+?)\*\*|__(.+?)__|(?<![\w*])\*(?![\s*])(.+?)(?<![\s*])\*(?![\w*])", "$1$2$3")
            Regex.Replace(p, @"\$([^$\n]+)\$", fun m -> speakLatex m.Groups.[1].Value)
        | Piece.Formula f -> speakLatex f + ".")
    |> String.concat " "

/// The answer as clips to synthesize one after another: a short first one so reading starts quickly,
/// then a few sentences each.
let speechChunks (answer: string) : string list =
    let sentences =
        Regex.Split(spoken answer, @"(?<=[.!?:])\s+(?=[A-Z0-9(“""])")
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s <> "")
    let chunks = ResizeArray<string>()
    let current = StringBuilder()
    for s in sentences do
        let limit = if chunks.Count = 0 then 120 else 320
        if current.Length > 0 && current.Length + s.Length > limit then
            chunks.Add(current.ToString())
            current.Clear() |> ignore
        if current.Length > 0 then current.Append(' ') |> ignore
        current.Append(s) |> ignore
    if current.Length > 0 then chunks.Add(current.ToString())
    List.ofSeq chunks

// ---------------------------------------------------------------------------------------------
// One-tap questions for the moment
// ---------------------------------------------------------------------------------------------

let private termRx = Regex(@"(?<![\w-])([A-Z][a-z]+[A-Z][A-Za-z]*|[A-Z]{2,}[a-z]?)(-\d+)?(?![\w-])", RegexOptions.Compiled)

let private notTerms = set [ "II"; "III"; "IV"; "VI"; "VII"; "VIII"; "IX"; "XI"; "OK"; "US"; "UK"; "EU"; "AND"; "OR"; "NOT"; "THE" ]

/// Jargon in the text just heard (acronyms and CamelCase names like ReLU, BLEU, ImageNet, CIFAR-10),
/// offered as "What is …?" taps. Newest first.
let terms (texts: string list) : string list =
    texts
    |> List.rev
    |> List.collect (fun t -> [ for m in termRx.Matches t -> m.Value ])
    |> List.filter (fun t -> not (notTerms.Contains t) && t.Length <= 24)
    |> List.map (fun t -> if t.EndsWith "s" && t.Length > 3 && Char.IsUpper t.[t.Length - 2] then t.Substring(0, t.Length - 1) else t)
    |> List.distinct
    |> List.truncate 3

/// The taps offered before anything is asked: depends on what is on screen and what was just said.
let suggestions (script: Script) (position: int) (about: Visual option) : Ask list =
    let segs = script.Segments
    let position = max 0 (min position (segs.Length - 1))
    let recent = [ for i in max 0 (position - 2) .. position -> segs.[i].Say ]
    [ match about with
      | Some v when v.Kind <> VisualKind.Inline -> yield Ask.Walkthrough
      | _ -> ()
      yield Ask.Simpler
      yield Ask.Example
      yield Ask.WhyItMatters
      for t in terms recent do yield Ask.Define t
      yield Ask.Recap ]

// ---------------------------------------------------------------------------------------------
// Preparing the knowledge
// ---------------------------------------------------------------------------------------------

/// Detailed descriptions of the figures, from a vision model, for the answering model that can't see them.
let describeFigures (key: string) (model: string) (figures: (Visual * byte[]) list) (ct: CancellationToken) : Task<Map<string, string>> =
    task {
        let system =
            """You describe figures from a research paper for someone who cannot see them but will ask questions about them.
For each image write a factual description of 80 to 200 words: the kind of figure (plot, diagram, photo grid...),
axes with their labels and units, legend entries and series, notable values, trends and comparisons, the parts
of a diagram and how they connect, and what the caption says it demonstrates. Don't guess numbers you can't read.
Answer in JSON: {"<id>": "<description>", ...} with the ids given."""
        let mutable notes = Map.empty
        for batch in figures |> List.chunkBySize 6 do
            let parts =
                [ for v, png in batch do
                      yield Mistral.Text(sprintf "Image id %s (%s). Caption: %s" v.Id (visualName v) (oneLine v.RawText))
                      yield Mistral.Png png ]
            let! json = Mistral.chatJson key model system parts ct
            use d = Text.Json.JsonDocument.Parse json
            for p in d.RootElement.EnumerateObject() do
                if p.Value.ValueKind = Text.Json.JsonValueKind.String then notes <- notes.Add(p.Name, p.Value.GetString())
        return notes
    }

/// Loads what the model needs to answer about a paper, making what is missing (one OCR call, one or two
/// vision calls) and caching it in the paper's folder.
let prepare (settings: Settings) (paths: Store.Paths) (id: string) (script: Script) (step: string -> unit) (ct: CancellationToken) : Task<Knowledge> =
    task {
        let key = settings.MistralApiKey
        let! text =
            task {
                if File.Exists(paths.PaperText id) then return File.ReadAllText(paths.PaperText id)
                else
                    step "Reading the paper for your questions"
                    let! pages = Mistral.ocr key "application/pdf" (File.ReadAllBytes(paths.Pdf id)) ct
                    let text = paperText pages
                    File.WriteAllText(paths.PaperText id, text)
                    return text
            }
        let! notes =
            task {
                match Store.loadFigureNotes paths id with
                | Some n -> return n
                | None ->
                    let figures =
                        [ for v in script.Visuals do
                              if v.Kind = VisualKind.Figure && File.Exists(paths.Image(id, v.Id)) then
                                  yield v, File.ReadAllBytes(paths.Image(id, v.Id)) ]
                    if figures.IsEmpty then return Map.empty
                    else
                        step "Looking at the figures"
                        try
                            let! n = describeFigures key settings.NarrationModel figures ct
                            Store.saveFigureNotes paths id n
                            return n
                        with
                        | :? OperationCanceledException -> return raise (OperationCanceledException())
                        | _ -> return Map.empty // answers still work from the captions; tried again next time
            }
        return { PaperText = text; FigureNotes = notes }
    }

/// Asks the model; `onText` gets the visible answer as it streams in.
let ask (settings: Settings) (script: Script) (k: Knowledge) (history: HelpTurn list) (position: int) (about: Visual option)
        (question: Ask) (onText: string -> unit) (ct: CancellationToken) : Task<HelpTurn> =
    task {
        let q = questionText question about
        let msgs = messages settings script k history position about q
        let! text = Mistral.chatStream settings.MistralApiKey settings.HelpModel msgs (if settings.HelpModel.Contains "glm" then Some(effort question) else None) (visibleAnswer >> onText) ct
        let reply = parseReply script text
        if reply.Answer = "" then failwith "The model sent an empty answer. Try again."
        return
            { Question = q
              Answer = reply.Answer
              Segment = position
              About = about |> Option.map (fun v -> v.Id)
              Show = reply.Show
              Followups = reply.Followups
              AskedUtc = DateTime.UtcNow }
    }
