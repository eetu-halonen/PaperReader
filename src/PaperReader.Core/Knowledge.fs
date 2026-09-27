/// What the learner knows across papers (Study): the ideas they have studied, each with the same memory state as a
/// flashcard (Fsrs) and a pool of questions from every paper it was met in. It decides what a new paper can skip,
/// picks a different question each time an idea comes back, and makes one review queue with the flashcards.
module PaperReader.Core.Knowledge

open System

/// The chance of recalling the idea now (FSRS); 0 before it was ever checked.
let strength (now: DateTime) (c: Concept) =
    match c.Memory.LastReview with
    | Some t when c.Memory.Stage <> CardStage.New -> Fsrs.recall c.Memory.Stability (now - t).TotalDays
    | _ -> 0.0

/// Known well enough that a new paper using it can skip it: checked before and not due for review. An idea that
/// is due (its recall has fallen to the retention in Settings) gets one quick question instead, which is its review.
let skippable (now: DateTime) (c: Concept) =
    c.Memory.Stage <> CardStage.New && not (Fsrs.isDue now c.Memory)

/// A multiple-choice answer as a review: right is Good; wrong, or "I don't know", is Again.
let rating (q: Question) (choice: int option) =
    if choice = Some q.Correct then Fsrs.Rating.Good else Fsrs.Rating.Again

/// The idea with one of its questions marked as asked at `now`, its memory unchanged.
let asked (now: DateTime) (questionId: string) (c: Concept) =
    { c with Questions = c.Questions |> List.map (fun q -> if q.Id = questionId then { q with LastAsked = Some now } else q) }

/// The idea after one of its questions was answered at `now`.
let answer (retention: float) (now: DateTime) (rating: Fsrs.Rating) (questionId: string) (c: Concept) =
    { asked now questionId c with Memory = Fsrs.review retention now c.Id c.Memory rating }

/// The question to ask next about an idea: of the other kind than the one asked last (recall after multiple
/// choice, and back), then the one not asked for longest, so each review retrieves the idea a different way.
let pick (multipleChoiceOnly: bool) (exclude: Set<string>) (c: Concept) : Question option =
    let lastWasChoice =
        c.Questions
        |> List.filter (fun q -> q.LastAsked.IsSome)
        |> List.sortByDescending (fun q -> q.LastAsked)
        |> List.tryHead
        |> Option.map (fun q -> not q.Options.IsEmpty)
    c.Questions
    |> List.filter (fun q -> not (exclude.Contains q.Id) && (not multipleChoiceOnly || not q.Options.IsEmpty))
    |> List.sortBy (fun q ->
        let sameKind = lastWasChoice = Some(not q.Options.IsEmpty)
        sameKind, q.LastAsked)
    |> List.tryHead

/// Questions kept per idea; the oldest go first.
let maxQuestions = 12

/// Adds questions (from a lesson in another paper, or the same one again) to an idea's pool.
let addQuestions (questions: Question list) (c: Concept) =
    let have = c.Questions |> List.map (fun q -> q.Id) |> Set.ofList
    let all = c.Questions @ (questions |> List.filter (fun q -> not (have.Contains q.Id)))
    { c with Questions = if all.Length > maxQuestions then List.skip (all.Length - maxQuestions) all else all }

let addSource (s: ConceptSource) (c: Concept) =
    { c with Sources = s :: (c.Sources |> List.filter (fun x -> x.PaperId <> s.PaperId)) }

/// A new idea, first met and checked in a lesson.
let create (now: DateTime) (name: string) (definition: string) (background: bool) (source: ConceptSource) (questions: Question list) =
    { Id = Guid.NewGuid().ToString("N")
      Name = name
      Definition = definition
      Background = background
      Sources = [ source ]
      Questions = []
      Memory = Fsrs.fresh now
      CreatedUtc = now }
    |> addQuestions questions

// ---------------------------------------------------------------------------------------------
// Reviews: ideas and flashcards together
// ---------------------------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type ReviewItem =
    | Card of paperId: string * Card
    /// An idea, asked with one of its questions.
    | Concept of Concept * Question

let private memoryOf =
    function
    | ReviewItem.Card (_, c) -> c.Memory
    | ReviewItem.Concept (c, _) -> c.Memory

/// Everything due now: short learning steps first, then reviews most likely to be forgotten (ideas and cards of
/// every paper mixed), then at most `Cards.newPerSession` new cards in the order they were made.
let queue (now: DateTime) (decks: (string * Card list) list) (concepts: Concept list) : ReviewItem list =
    let items =
        [ for id, cards in decks do
              for c in cards do
                  if Fsrs.isDueForReview now c.Memory then yield ReviewItem.Card(id, c)
          for c in concepts do
              if c.Memory.Stage <> CardStage.New && Fsrs.isDueForReview now c.Memory then
                  match pick false Set.empty c with
                  | Some q -> yield ReviewItem.Concept(c, q)
                  | None -> () ]
    let stage (i: ReviewItem) = (memoryOf i).Stage
    let steps, rest = items |> List.partition (fun i -> stage i = CardStage.Learning || stage i = CardStage.Relearning)
    let reviews, fresh = rest |> List.partition (fun i -> stage i = CardStage.Review)
    let recallNow (i: ReviewItem) =
        let m = memoryOf i
        match m.LastReview with
        | Some t -> Fsrs.recall m.Stability (now - t).TotalDays
        | None -> 0.0
    let made =
        function
        | ReviewItem.Card (_, c) -> c.CreatedUtc, c.Segment
        | ReviewItem.Concept (c, _) -> c.CreatedUtc, 0
    (steps |> List.sortBy (fun i -> (memoryOf i).Due))
    @ (reviews |> List.sortBy recallNow)
    @ (fresh |> List.sortBy made |> List.truncate Cards.newPerSession)

/// The ideas met in a paper.
let ofPaper (paperId: string) (concepts: Concept list) =
    concepts |> List.filter (fun c -> c.Sources |> List.exists (fun s -> s.PaperId = paperId))

/// Where to listen to an idea: the paper it was met in most recently that is still in the library.
let whereToListen (library: string -> bool) (c: Concept) =
    c.Sources |> List.tryFind (fun s -> library s.PaperId)
