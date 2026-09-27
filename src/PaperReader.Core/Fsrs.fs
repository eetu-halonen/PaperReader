/// Spaced repetition with FSRS-5 (Free Spaced Repetition Scheduler, the scheduler in Anki since 23.10).
///
/// Every card has a stability S (days until the chance of recalling it falls to 90%) and a difficulty D (1 to 10).
/// The chance of recall after t days is R = (1 + F·t/S)^-0.5. Each review updates S and D from the answer and
/// from R at that moment: a card remembered when it was nearly forgotten gains much more stability than one
/// reviewed too early, and hard cards gain less. The next review is when R will have fallen to the desired
/// retention (90% by default), so each card comes back just before it would be forgotten. Compared with SM-2's
/// fixed ease multipliers this needs roughly 20–30% fewer reviews for the same retention.
///
/// New and forgotten cards go through short steps first (1 and 10 minutes, as Anki does), so they are seen
/// again in the same session until they are known.
module PaperReader.Core.Fsrs

open System

[<RequireQualifiedAccess>]
type Rating =
    /// Forgot it.
    | Again
    /// Remembered with serious effort.
    | Hard
    /// Remembered.
    | Good
    /// Remembered at once.
    | Easy

let ratings = [ Rating.Again; Rating.Hard; Rating.Good; Rating.Easy ]

let private grade =
    function
    | Rating.Again -> 1.0
    | Rating.Hard -> 2.0
    | Rating.Good -> 3.0
    | Rating.Easy -> 4.0

/// FSRS-5 default parameters, fitted on hundreds of millions of Anki reviews.
let private w =
    [| 0.40255; 1.18385; 3.173; 15.69105; 7.1949; 0.5345; 1.4604; 0.0046; 1.54575; 0.1192
       1.01925; 1.9395; 0.11; 0.29605; 2.2698; 0.2315; 2.9898; 0.51655; 0.6621 |]

let private decay = -0.5
let private factor = Math.Pow(0.9, 1.0 / decay) - 1.0 // 19/81, so that R = 90% when t = S
let maxIntervalDays = 36500.0

let private minStability = 0.01
let private clampD (d: float) = min 10.0 (max 1.0 d)

/// Chance of recalling the card after `days`.
let recall (stability: float) (days: float) =
    if stability <= 0.0 then 0.0 else Math.Pow(1.0 + factor * max 0.0 days / stability, decay)

/// Days until the chance of recall falls to `retention`.
let intervalDays (retention: float) (stability: float) =
    stability / factor * (Math.Pow(retention, 1.0 / decay) - 1.0)

let private initialStability (r: Rating) = max minStability w.[int (grade r) - 1]

let private initialDifficulty (r: Rating) = clampD (w.[4] - Math.Exp(w.[5] * (grade r - 1.0)) + 1.0)

let private nextDifficulty (d: float) (r: Rating) =
    let delta = -w.[6] * (grade r - 3.0)
    // the closer to 10, the smaller the step, so difficulty never saturates
    let d' = d + delta * (10.0 - d) / 9.0
    // drifts back towards the difficulty of a card first answered Easy
    clampD (w.[7] * initialDifficulty Rating.Easy + (1.0 - w.[7]) * d')

let private recallStability (d: float) (s: float) (r: float) (rating: Rating) =
    let hard = if rating = Rating.Hard then w.[15] else 1.0
    let easy = if rating = Rating.Easy then w.[16] else 1.0
    s * (1.0 + Math.Exp w.[8] * (11.0 - d) * Math.Pow(s, -w.[9]) * (Math.Exp(w.[10] * (1.0 - r)) - 1.0) * hard * easy)

let private forgetStability (d: float) (s: float) (r: float) =
    let f = w.[11] * Math.Pow(d, -w.[12]) * (Math.Pow(s + 1.0, w.[13]) - 1.0) * Math.Exp(w.[14] * (1.0 - r))
    min f s

/// Reviews on the same day barely change what is remembered a week later.
let private sameDayStability (s: float) (rating: Rating) = s * Math.Exp(w.[17] * (grade rating - 3.0 + w.[18]))

let private learnAgain = TimeSpan.FromMinutes 1.0
let private learnHard = TimeSpan.FromMinutes 6.0
let private learnGood = TimeSpan.FromMinutes 10.0
let private relearn = TimeSpan.FromMinutes 10.0

/// A card that has never been reviewed, due now.
let fresh (now: DateTime) : Memory =
    { Stage = CardStage.New
      Due = now
      Stability = 0.0
      Difficulty = 0.0
      Reps = 0
      Lapses = 0
      LastReview = None }

/// Spreads intervals of three days or more by up to ±5% (the same for the same card and review), so cards
/// made together don't keep coming back on the same day.
let private fuzz (seed: string) (days: float) =
    if days < 3.0 then days
    else
        let h = seed |> Seq.fold (fun (h: uint32) c -> (h ^^^ uint32 c) * 16777619u) 2166136261u
        let unit = float (h % 10000u) / 9999.0 // 0..1
        days * (0.95 + 0.1 * unit)

/// Whole days, at least `atLeast`.
let private days (seed: string) (atLeast: float) (d: float) =
    Math.Round(fuzz seed d) |> max atLeast |> max 1.0 |> min maxIntervalDays

/// The card's memory after answering it with `rating` at `now`. `seed` (the card's id) makes the fuzz repeatable.
let review (retention: float) (now: DateTime) (seed: string) (m: Memory) (rating: Rating) : Memory =
    let elapsed = match m.LastReview with Some t -> (now - t).TotalDays | None -> 0.0
    let seed = sprintf "%s/%d" seed m.Reps
    let s, d =
        match m.Stage with
        | CardStage.New -> initialStability rating, initialDifficulty rating
        | _ when elapsed < 1.0 -> sameDayStability m.Stability rating, nextDifficulty m.Difficulty rating
        | _ ->
            let r = recall m.Stability elapsed
            (if rating = Rating.Again then forgetStability m.Difficulty m.Stability r
             else recallStability m.Difficulty m.Stability r rating),
            nextDifficulty m.Difficulty rating
    let s = max minStability s
    let ivl = intervalDays retention s
    // Hard < Good < Easy, whatever the stabilities say
    let orderedDays (r: Rating) =
        let sFor (x: Rating) =
            match m.Stage with
            | CardStage.New -> initialStability x
            | _ when elapsed < 1.0 -> sameDayStability m.Stability x
            | _ -> recallStability m.Difficulty m.Stability (recall m.Stability elapsed) x
        let hard = days seed 1.0 (intervalDays retention (max minStability (sFor Rating.Hard)))
        let good = days seed (hard + (if m.Stage = CardStage.Review then 1.0 else 0.0)) (intervalDays retention (max minStability (sFor Rating.Good)))
        let easy = days seed (good + 1.0) (intervalDays retention (max minStability (sFor Rating.Easy)))
        match r with
        | Rating.Hard -> min hard good
        | Rating.Good -> good
        | _ -> easy
    let next =
        { m with
            Stability = s
            Difficulty = d
            Reps = m.Reps + 1
            LastReview = Some now }
    match m.Stage, rating with
    // learning: short steps until it is remembered twice, or once Easy
    | CardStage.New, Rating.Again
    | CardStage.Learning, Rating.Again -> { next with Stage = CardStage.Learning; Due = now + learnAgain }
    | CardStage.New, Rating.Hard
    | CardStage.Learning, Rating.Hard -> { next with Stage = CardStage.Learning; Due = now + learnHard }
    | CardStage.New, Rating.Good -> { next with Stage = CardStage.Learning; Due = now + learnGood }
    | CardStage.Relearning, Rating.Again -> { next with Due = now + relearn }
    | CardStage.Relearning, Rating.Hard -> { next with Due = now + learnHard }
    | CardStage.Review, Rating.Again ->
        { next with Stage = CardStage.Relearning; Due = now + relearn; Lapses = m.Lapses + 1 }
    | CardStage.Review, _ -> { next with Due = now + TimeSpan.FromDays(orderedDays rating) }
    | _, _ ->
        // graduates (Good from a step, or Easy)
        let d = if rating = Rating.Easy then orderedDays Rating.Easy else days seed 1.0 ivl
        { next with Stage = CardStage.Review; Due = now + TimeSpan.FromDays d }

/// When the card would come back for each answer, for showing on the buttons.
let preview (retention: float) (now: DateTime) (seed: string) (m: Memory) : (Rating * TimeSpan) list =
    [ for r in ratings -> r, (review retention now seed m r).Due - now ]

/// A card is due when its time has come; cards learned in days are due all of their day.
let isDue (now: DateTime) (m: Memory) =
    match m.Stage with
    | CardStage.Review -> m.Due.ToLocalTime().Date <= now.ToLocalTime().Date
    | _ -> m.Due <= now

/// How far ahead a review takes cards in their short steps, as Anki does: closing a review and opening it again
/// keeps the card that was coming back at its end, instead of hiding it until its minutes are up.
let learnAhead = TimeSpan.FromMinutes 20.0

/// Due in a review session: due now, or in a short step that ends within `learnAhead`.
let isDueForReview (now: DateTime) (m: Memory) =
    match m.Stage with
    | CardStage.Learning
    | CardStage.Relearning -> m.Due <= now + learnAhead
    | _ -> isDue now m

/// "1 min", "10 min", "3 h", "4 d", "2.5 mo", "1.2 y".
let formatInterval (t: TimeSpan) =
    let inv (x: float) = x.ToString("0.#", Globalization.CultureInfo.InvariantCulture)
    if t.TotalMinutes < 59.5 then sprintf "%d min" (max 1 (int (Math.Round t.TotalMinutes)))
    elif t.TotalHours < 23.5 then sprintf "%d h" (int (Math.Round t.TotalHours))
    elif t.TotalDays < 30.0 then sprintf "%d d" (int (Math.Round t.TotalDays))
    elif t.TotalDays < 365.0 then inv (t.TotalDays / 30.4) + " mo"
    else inv (t.TotalDays / 365.25) + " y"
