/// Flashcards made by the model from the paper: about what the listener asks to remember, about the moment
/// they are at, from an answer in Ask, or a deck from the whole paper. Reviews are scheduled by Fsrs.
///
/// The model gets the same system prompt as Ask (the whole paper, every equation, figure and table), so the
/// API's cache of that long prefix serves both; the request says what cards to make and in what format.
module PaperReader.Core.Cards

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// What to make cards about.
[<RequireQualifiedAccess>]
type Request =
    /// Typed by the listener: "the three kinds of attention", "why divide by sqrt d_k".
    | Topic of text: string
    /// The sentences just heard.
    | Moment
    /// The equation, figure or table on screen or picked.
    | Visual of id: string
    /// The main points of the section being listened to.
    | Section
    /// An exchange in Ask worth keeping.
    | Answer of HelpTurn
    /// A deck covering the whole paper, of about this many cards.
    | Paper of count: int

/// How the request is shown while cards are made, and kept as the card's origin.
let describe (script: Script) (request: Request) =
    match request with
    | Request.Topic t -> t.Trim()
    | Request.Moment -> "What you just heard"
    | Request.Visual id ->
        match script.Visual id with
        | Some v -> Help.visualName v
        | None -> "This equation"
    | Request.Section -> "This section's main points"
    | Request.Answer t -> t.Question
    | Request.Paper _ -> "The whole paper"

let private principles =
    """GOOD CARDS
- One idea per card. A question with one short answer (usually one sentence, at most about 30 words). Split
  anything longer or any list into several cards.
- The question is precise enough to have one right answer, and not answerable by yes or no.
- Each card is reviewed months from now, shuffled with cards from other papers and documents, without the paper
  at hand: the question must make sense alone. Name the method, model, idea, book or person it is about ("In the
  Transformer, ...") instead of "this paper", "the authors" or "the equation above".
- Ask for understanding, not wording: what something is, why it is done, what it prevents, how it compares,
  what a symbol or term stands for, what a result shows (with its key number when the number matters).
- Use the paper's notation and numbers. Never invent anything the paper doesn't say.
- Math as inline LaTeX between single dollars ($\sqrt{d_k}$); a short formula can be the answer. No $$ blocks.
- An equation, figure or table can go with a card ("show"): on the front when the question is about it ("What
  does the $\sqrt{d_k}$ in this equation prevent?"), on the back when it illustrates the answer.
"""

let private format (sections: bool) =
    let section =
        if sections then "\n      \"section\": <the number of the section it is from, from SECTIONS>," else ""
    $$"""REPLY FORMAT
Only JSON, nothing before or after it:
{"cards": [
    { "front": "<question>",
      "back": "<answer>",{{section}}
      "show": "<id of an equation, figure or table from VISUALS, or null>",
      "showOn": "front" or "back" }
]}"""

let private existingBlock (existing: Card list) =
    if existing.IsEmpty then ""
    else
        let sb = StringBuilder("\nCARDS ALREADY MADE FROM THIS PAPER (don't repeat these; cover something else)\n")
        for c in existing |> List.truncate 120 do
            sb.Append("- ").Append(Regex.Replace(c.Front, @"\s+", " ")).Append('\n') |> ignore
        sb.ToString()

let sectionsBlock (script: Script) =
    let sb = StringBuilder("SECTIONS\n")
    for i in 1 .. script.Sections.Length - 1 do
        sb.AppendFormat("{0}: {1}\n", i, script.Sections.[i].Title) |> ignore
    sb.ToString()

/// The request, after the same system prompt as Ask.
let userPrompt (script: Script) (position: int) (existing: Card list) (request: Request) =
    let task (what: string) =
        sprintf "TASK: this time don't answer a question. Make flashcards for the listener to learn from, with spaced repetition. \
                 Ignore the length rules and the REPLY FORMAT above.\n\n%s\n%s\n%s%s"
            what principles (format false) (existingBlock existing)
    let atPosition (what: string) (about: Visual option) =
        // where the listener is, as Ask describes it; the "question" is the card request
        Help.userPrompt script position about (task what)
    match request with
    | Request.Topic t ->
        atPosition
            (sprintf "The listener wants to remember: \"%s\". Make the cards that capture it (usually 1 to 3), as the paper \
                      explains it. If it is about where they are listening, use that context." (t.Trim()))
            None
    | Request.Moment ->
        atPosition "Make 1 to 3 cards on what the listener just heard: the idea worth remembering in the last sentences, not details." None
    | Request.Visual id ->
        atPosition
            "Make 1 to 3 cards on the equation, figure or table the question is about: what it expresses or shows, \
             what its parts mean, why it has that form, and its takeaway. Show it on the cards."
            (script.Visual id)
    | Request.Section ->
        atPosition "Make 3 to 6 cards on the main points of the section the listener is in, as far as they have heard it and to its end." None
    | Request.Answer turn ->
        atPosition
            (sprintf "The listener asked \"%s\" and was answered:\n\"\"\"\n%s\n\"\"\"\nMake 1 or 2 cards that keep what they learned \
                      from this answer." turn.Question turn.Answer)
            (turn.About |> Option.bind script.Visual)
    | Request.Paper count ->
        sprintf "TASK: this time don't answer a question. Make a deck of about %d flashcards that covers the whole paper, for \
                 learning it with spaced repetition. Ignore the length rules and the REPLY FORMAT above.\n\n\
                 For a research paper, cover in the paper's order: the problem and why it matters, the key idea and how it \
                 differs from earlier work, the method's parts and the important equations (what they compute and why they \
                 have that form), the main results with their numbers, and the limitations. For any other document (a book, \
                 an article, a report, slides, notes), cover in its order its main ideas and arguments, key facts, terms, \
                 people and events, and the important examples. Prefer what an expert would want remembered a year \
                 from now over details. Skip related work, experimental setup trivia and acknowledgements.\n\n%s\n%s\n%s%s"
            count principles (sectionsBlock script) (format true) (existingBlock existing)

let private normal (s: string) = Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim()

/// Reads the model's cards. Cards missing a side, and repeats of existing ones, are left out.
let parse (script: Script) (position: int) (origin: string) (existing: Card list) (now: DateTime) (text: string) : Card list =
    let text = Regex.Replace(text, @"^\s*```(?:json)?|```\s*$", "", RegexOptions.Multiline)
    let start = text.IndexOfAny [| '{'; '[' |]
    let stop = max (text.LastIndexOf '}') (text.LastIndexOf ']')
    if start < 0 || stop <= start then []
    else
        try
            use d = JsonDocument.Parse(text.Substring(start, stop - start + 1))
            let items =
                match d.RootElement.ValueKind with
                | JsonValueKind.Array -> d.RootElement
                | _ ->
                    match d.RootElement.TryGetProperty "cards" with
                    | true, a when a.ValueKind = JsonValueKind.Array -> a
                    | _ -> JsonDocument.Parse("[]").RootElement
            let field (e: JsonElement) (name: string) =
                match e.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString().Trim()
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetRawText()
                | _ -> ""
            let seen = Collections.Generic.HashSet<string>(existing |> List.map (fun c -> normal c.Front))
            [ for e in items.EnumerateArray() do
                  if e.ValueKind = JsonValueKind.Object then
                      let front, back = field e "front", field e "back"
                      if front <> "" && back <> "" && seen.Add(normal front) then
                          let show =
                              match field e "show" with
                              | "" | "null" | "none" -> None
                              | id -> Some(id.Trim('[', ']', ' ')) |> Option.filter (fun id -> script.Visual id |> Option.isSome)
                          let segment =
                              match Int32.TryParse(field e "section") with
                              | true, s when s > 0 && s < script.Sections.Length -> script.Sections.[s].FirstSegment
                              | _ ->
                                  // a card showing an equation belongs where the equation is read
                                  match show |> Option.bind (fun id -> script.Segments |> Array.tryFind (fun g -> g.Show = Some id)) with
                                  | Some g when origin = "paper" -> g.Index
                                  | _ -> position
                          yield
                              { Id = Guid.NewGuid().ToString("N")
                                Front = front
                                Back = back
                                Visual = show
                                VisualOnFront = show.IsSome && field e "showOn" = "front"
                                Segment = max 0 (min segment (script.Segments.Length - 1))
                                Origin = origin
                                CreatedUtc = now
                                Memory = Fsrs.fresh now } ]
        with _ -> []

/// Cards the model has written so far, from the reply being streamed.
let countSoFar (partial: string) = Regex.Matches(partial, "\"front\"\\s*:").Count

/// Asks the model for cards. `progress` gets the number written so far.
let make (settings: Settings) (script: Script) (k: Help.Knowledge) (position: int) (existing: Card list) (request: Request)
         (progress: int -> unit) (ct: CancellationToken) : Task<Card list> =
    task {
        let msgs = [ "system", Help.systemPrompt settings script k; "user", userPrompt script position existing request ]
        let effort =
            if not (settings.HelpModel.Contains "glm") then None
            else
                match request with
                | Request.Moment | Request.Answer _ -> Some "low"
                | _ -> Some "high"
        let mutable reported = 0
        let onText (t: string) =
            let n = countSoFar t
            if n <> reported then
                reported <- n
                progress n
        let! text = Mistral.chatStream settings.MistralApiKey settings.HelpModel msgs effort onText ct
        let origin = match request with Request.Paper _ -> "paper" | r -> describe script r
        let cards = parse script position origin existing DateTime.UtcNow text
        if cards.IsEmpty then
            failwith (if countSoFar text > 0 then "The model only made cards you already have." else "The model didn't send any cards. Try again.")
        return cards
    }

/// A deck size that fits the paper: about one card per page, 12 to 30.
let deckSize (script: Script) = script.PageCount |> max 12 |> min 30

// ---------------------------------------------------------------------------------------------
// Reviewing
// ---------------------------------------------------------------------------------------------

/// New cards taken into one review session, so a new deck doesn't bury the reviews that are due.
let newPerSession = 20

/// The cards due now, as (paper id, card): learning steps first, then reviews by how overdue they are, then new
/// cards in the order they were made (a paper's deck follows the paper).
let dueQueue (now: DateTime) (decks: (string * Card list) list) : (string * Card) list =
    let all = [ for id, cards in decks do for c in cards do if Fsrs.isDueForReview now c.Memory then yield id, c ]
    let learning, rest = all |> List.partition (fun (_, c) -> c.Memory.Stage = CardStage.Learning || c.Memory.Stage = CardStage.Relearning)
    let reviews, fresh = rest |> List.partition (fun (_, c) -> c.Memory.Stage = CardStage.Review)
    let byRecall =
        reviews
        |> List.sortBy (fun (_, c) ->
            let elapsed = match c.Memory.LastReview with Some t -> (now - t).TotalDays | None -> 0.0
            Fsrs.recall c.Memory.Stability elapsed)
    (learning |> List.sortBy (fun (_, c) -> c.Memory.Due))
    @ byRecall
    @ (fresh |> List.sortBy (fun (_, c) -> c.CreatedUtc, c.Segment) |> List.truncate newPerSession)

/// Cards due now, counting at most `newPerSession` new ones.
let dueCount (now: DateTime) (cards: Card list) = (dueQueue now [ "", cards ]).Length
