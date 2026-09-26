module PaperReader.Core.StudyTests

open System
open System.IO
open Xunit
open PaperReader.Core

let private t0 = DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc)

let private script () =
    let seg i section show = { Index = i; Kind = UnitKind.Sentence; Say = sprintf "s%d" i; Show = show; Reason = ShowReason.Own; Section = section; Page = 0; PauseAfterMs = 0 }
    let eq id = { Id = id; Kind = VisualKind.Equation; Page = 0; Parts = [||]; EqNumber = Some "1"; RawText = ""; Latex = Some "x" }
    { Version = 1; Title = "Attention"; PageCount = 3; Narrator = "test"
      Sections = [| { Title = "T"; FirstSegment = 0 }; { Title = "1 Intro"; FirstSegment = 0 }; { Title = "2 Attention"; FirstSegment = 2 } |]
      Segments = [| seg 0 1 None; seg 1 1 None; seg 2 2 None; seg 3 2 (Some "E1"); seg 4 2 None |]
      Visuals = [| eq "E1"; { eq "I1" with Kind = VisualKind.Inline } |] }

let private planText =
    """OVERVIEW: The paper replaces recurrence with attention.
It trains faster.

PART: The problem

IDEA c1: The RNN bottleneck
kind: background
core: no
requires: none
uses known: none
same as known: none
section: none
show: none
goal: Why recurrence is slow.
define: Recurrent networks compute one position at a time,
  so they can't be parallelized.

**IDEA 2:** Attention only
- kind: paper
- core: yes
- requires: c1, c9
- section: 1
- show: E1, I1, E7
goal: The key idea.
define: In the Transformer, attention replaces recurrence.

RECAP: Why drop recurrence?
POINTS: Parallelism and short paths.

PART: The method
IDEA c3 — Scaled dot-product attention
kind: paper
core: yes
requires: c2
same as known: kd0e
section: 2 Attention
show: E1
goal: Equation 1.
define: Softmax of scaled dot products.
RECAP: How does attention compute its output?
POINTS: Weights from queries and keys, a weighted sum of values.
"""

[<Fact>]
let ``a plan is read from the model's text, whatever markdown it wraps it in`` () =
    let plan = Study.parsePlan (script ()) planText
    Assert.Equal("The paper replaces recurrence with attention. It trains faster.", plan.Overview)
    Assert.Equal<string list>([ "The problem"; "The method" ], plan.Parts |> List.map (fun p -> p.Title))
    Assert.Equal<string list>([ "c1"; "c2"; "c3" ], plan.Ideas |> List.map (fun i -> i.Id))
    let c1, c2, c3 = plan.Ideas.[0], plan.Ideas.[1], plan.Ideas.[2]
    Assert.True(c1.Background)
    Assert.Equal("Recurrent networks compute one position at a time, so they can't be parallelized.", c1.Definition)
    Assert.Equal("Attention only", c2.Name)
    Assert.True(c2.Core)
    // an idea that isn't in the plan, and visuals that aren't shown (inline) or don't exist, are dropped
    Assert.Equal<string list>([ "c1" ], c2.Requires)
    Assert.Equal<string list>([ "E1" ], c2.Visuals)
    Assert.Equal(1, c2.Section)
    Assert.Equal("Scaled dot-product attention", c3.Name)
    Assert.Equal(2, c3.Section)
    Assert.Equal(Some "kd0e", c3.Same)
    Assert.Equal("Why drop recurrence?", plan.Parts.[0].Recap)
    Assert.Equal("Weights from queries and keys, a weighted sum of values.", plan.Parts.[1].Points)
    Assert.Equal<string list>([ "The RNN bottleneck"; "Attention only"; "Scaled dot-product attention" ], Study.planSoFar planText)

let private concept (id: string) (name: string) (m: Memory) =
    { Id = id; Name = name; Definition = name + " defined"; Background = false; Sources = []; Questions = []; Memory = m; CreatedUtc = t0 }

[<Fact>]
let ``known ideas are named by alias for the model, and by the learner's own ids in the saved plan`` () =
    let known = Study.aliases [ concept "aaa" "Softmax" (Fsrs.review 0.9 t0 "x" (Fsrs.fresh t0) Fsrs.Rating.Good) ]
    Assert.Equal("k1", fst known.Head)
    let text = "IDEA c1: X\nuses known: k1, k7\nsame as known: k1 (Softmax)\nIDEA c2: Y\nsame as known: k1"
    let resolved = Study.resolveKnown known text
    Assert.Contains("uses known: aaa\n", resolved)
    Assert.Contains("same as known: aaa\nIDEA c2", resolved)
    // only confirmed identities stay
    let kept = Study.keepSame (Set.ofList [ "c2" ]) resolved
    Assert.Contains("IDEA c1: X\nuses known: aaa\nsame as known: none\n", kept)
    Assert.EndsWith("same as known: aaa", kept)

let private lessonText =
    """SHOW: E1
EXPLAIN:
Attention compares a **query** with every key.

Then it averages the values: $\mathrm{softmax}(QK^T/\sqrt{d_k})V$.
EXAMPLE:
With $d_k = 4$, dividing by 2 keeps scores small.
**QUESTION 1:** Why divide by $\sqrt{d_k}$?
WITH: E1
A) To make the weights sum to one
B) To keep large dot products from saturating the softmax
C) To speed it up
D. To add a bias
RIGHT: B
why a: The softmax always sums to one.
WHY B: Right: large scores give tiny gradients,
which slows learning.
WHY C: One division costs nothing.
WHY D: Nothing is added.
QUESTION: What does $QK^T$ hold?
A: Scores
B: Gradients
RIGHT: A
QUESTION: A question without its answer
A: one
B: two
RECALL: State Equation 1.
ANSWER: $\mathrm{softmax}(QK^T/\sqrt{d_k})V$
"""

[<Fact>]
let ``a lesson is read with its questions, even written loosely`` () =
    let l = Study.parseLesson (script ()) "p" "c3" lessonText
    Assert.Equal(Some "E1", l.Show)
    Assert.Equal("Attention compares a **query** with every key.\n\nThen it averages the values: $\\mathrm{softmax}(QK^T/\\sqrt{d_k})V$.", l.Explanation)
    Assert.Equal("With $d_k = 4$, dividing by 2 keeps scores small.", l.Example)
    // the third question has no right answer, so it is left out
    Assert.Equal(2, l.Checks.Length)
    let q = l.Checks.[0]
    Assert.Equal("p:c3:1", q.Id)
    Assert.Equal("Why divide by $\\sqrt{d_k}$?", q.Prompt)
    Assert.Equal(Some "E1", q.Visual)
    Assert.Equal(4, q.Options.Length)
    Assert.Equal("To add a bias", q.Options.[3])
    Assert.Equal(1, q.Correct)
    Assert.Equal("The softmax always sums to one.", q.Why.[0])
    Assert.Equal("Right: large scores give tiny gradients, which slows learning.", q.Why.[1])
    let q2 = l.Checks.[1]
    Assert.Equal(0, q2.Correct)
    Assert.Equal<string list>([ ""; "" ], q2.Why)
    Assert.Equal(Some "p:c3:r", l.Recall |> Option.map (fun r -> r.Id))
    Assert.Equal("$\\mathrm{softmax}(QK^T/\\sqrt{d_k})V$", l.Recall.Value.Answer)
    Assert.True(l.Recall.Value.Options.IsEmpty)

[<Fact>]
let ``a lesson being written shows its explanation so far`` () =
    let partial = lessonText.Substring(0, lessonText.IndexOf "Then")
    let l = Study.parseLesson (script ()) "p" "c3" partial
    Assert.Equal("Attention compares a **query** with every key.", l.Explanation)
    Assert.Empty(l.Checks)

[<Fact>]
let ``feedback on a recap holds back its verdict until the line is whole`` () =
    let nothing: Study.Feedback = { Verdict = None; Text = "" }
    Assert.Equal(nothing, Study.parseFeedback "VERD")
    Assert.Equal(nothing, Study.parseFeedback "VERDICT: par")
    let f = Study.parseFeedback "\n**VERDICT:** partly\nYou named the speed-up. Missing: the cost."
    Assert.Equal(Some Study.Verdict.Partly, f.Verdict)
    Assert.Equal("You named the speed-up. Missing: the cost.", f.Text)
    Assert.Equal(Some Study.Verdict.NotYet, (Study.parseFeedback "VERDICT: not yet\nx").Verdict)
    Assert.Equal(Some Study.Verdict.GotIt, (Study.parseFeedback "VERDICT: got it\nx").Verdict)
    // a reply without a verdict is still feedback
    Assert.Equal("Good answer.", (Study.parseFeedback "Good answer.").Text)

[<Fact>]
let ``matches and their confirmations are read`` () =
    Assert.Equal<(string * string) list>([ "c3", "k1"; "c5", "k2" ], Study.parseMatches "c3 = k1\nC5 -> k2\nnone")
    Assert.Equal<Map<string, bool>>(Map [ "c3", false; "c5", true ], Study.parseVerdicts "c3: no\nc5: **yes**")

// ---- the session

let private plan () = Study.parsePlan (script ()) (planText.Replace("same as known: kd0e", "same as known: none"))

let private learned (at: DateTime) = Fsrs.review 0.9 at "x" (Fsrs.fresh at) Fsrs.Rating.Good

let private graduated (at: DateTime) = Fsrs.review 0.9 (at.AddMinutes 10.0) "x" (learned at) Fsrs.Rating.Good

let private withQuestion (c: Concept) =
    { c with Questions = [ { Id = c.Id + "q"; Prompt = "?"; Options = [ "a"; "b" ]; Correct = 0; Why = [ ""; "" ]; Answer = ""; PaperId = "p"; Visual = None; LastAsked = None } ] }

[<Fact>]
let ``the paper is heard a section at a time: background before the section that needs it, each idea after its own`` () =
    let summary (stops: Study.Stop list) =
        stops |> List.map (fun st -> st.First, st.Last, st.Before |> List.map (fun i -> i.Id), st.After |> List.map (fun i -> i.Id))
    Assert.Equal<(int * int * string list * string list) list>([ 0, 1, [ "c1" ], [ "c2" ]; 2, 4, [], [ "c3" ] ], summary (Study.stops (script ()) (plan ())))
    // the rest of the paper after the last idea is heard too
    let firstPart = { plan () with Parts = [ (plan ()).Parts.[0] ] }
    Assert.Equal<(int * int * string list * string list) list>([ 0, 1, [ "c1" ], [ "c2" ]; 2, 4, [], [] ], summary (Study.stops (script ()) firstPart))

[<Fact>]
let ``a session goes through the paper, skips what is known, checks what is fading, and recaps each part`` () =
    let plan = plan ()
    let script = script ()
    let now = t0.AddDays 1.0
    let empty = Study.Progress.empty t0
    // the background the first section needs, then the section, then its idea
    Assert.Equal(Study.Step.Teach "c1", fst (Study.next now script plan empty Map.empty None))
    let background = { empty with Done = Map [ "c1", Study.Outcome.Learned ] }
    Assert.Equal(Study.Step.Listen(0, 1), fst (Study.next now script plan background Map.empty None))
    Assert.Equal(Study.Step.Listen(1, 1), fst (Study.next now script plan { background with Heard = 1 } Map.empty None))
    Assert.Equal(Study.Step.Teach "c2", fst (Study.next now script plan { background with Heard = 2 } Map.empty None))
    // c1 known and fresh: skipped (and marked), so the section is next
    let fresh = concept "k1" "RNN" (graduated now)
    let step, progress = Study.next now script plan { empty with Links = Map [ "c1", "k1" ] } (Map [ "k1", fresh ]) None
    Assert.Equal(Study.Step.Listen(0, 1), step)
    Assert.Equal(Some Study.Outcome.Known, progress.Done.TryFind "c1")
    // c1 known but due: one quick question
    let fading = concept "k1" "RNN" { graduated t0 with Due = now.AddDays -1.0 }
    Assert.Equal(Study.Step.Check("c1", "k1"), fst (Study.next now script plan { empty with Links = Map [ "c1", "k1" ] } (Map [ "k1", fading ]) None))
    // a known idea the learner wants taught anyway
    Assert.Equal(Study.Step.Teach "c1", fst (Study.next now script plan { empty with Links = Map [ "c1", "k1" ]; Teach = set [ "c1" ] } (Map [ "k1", fresh ]) None))
    // after the first part: its recap, unless every idea in it was known already
    let doneFirst = { empty with Heard = 2; Done = Map [ "c1", Study.Outcome.Learned; "c2", Study.Outcome.Learned ] }
    Assert.Equal(Study.Step.Recap 0, fst (Study.next now script plan doneFirst Map.empty None))
    let knewFirst = { empty with Heard = 2; Done = Map [ "c1", Study.Outcome.Known; "c2", Study.Outcome.Known ] }
    Assert.Equal(Study.Step.Listen(2, 4), fst (Study.next now script plan knewFirst Map.empty None))
    let all = { doneFirst with Heard = 5; Done = doneFirst.Done.Add("c3", Study.Outcome.Learned); Recaps = set [ 0; 1 ] }
    Assert.Equal(Study.Step.Finished, fst (Study.next now script plan all Map.empty None))

[<Fact>]
let ``an idea learned minutes ago comes back between the others, but not straight after itself`` () =
    let plan = plan ()
    let script = script ()
    let justLearned = withQuestion (concept "k1" "RNN" (learned t0))
    let progress = { Study.Progress.empty t0 with Links = Map [ "c1", "k1" ]; Done = Map [ "c1", Study.Outcome.Learned ]; Heard = 2 }
    let concepts = Map [ "k1", justLearned ]
    // its 10 minute step
    Assert.Equal(Study.Step.Teach "c2", fst (Study.next (t0.AddMinutes 5.0) script plan progress concepts None))
    Assert.Equal(Study.Step.Review "k1", fst (Study.next (t0.AddMinutes 11.0) script plan progress concepts None))
    Assert.Equal(Study.Step.Teach "c2", fst (Study.next (t0.AddMinutes 11.0) script plan progress concepts (Some "k1")))
    // with no question left to ask it, it waits for the reviews
    Assert.Equal(Study.Step.Teach "c2", fst (Study.next (t0.AddMinutes 11.0) script plan { progress with Reported = set [ "k1q" ] } concepts None))

[<Fact>]
let ``the lessons written ahead are the ideas to be taught, not the known ones`` () =
    let plan = plan ()
    let progress = { Study.Progress.empty t0 with Links = Map [ "c2", "k2" ] }
    Assert.Equal<string list>([ "c1"; "c3" ], Study.upcoming (script ()) plan progress (Map [ "k2", concept "k2" "A" (graduated t0) ]) 3)
    Assert.Equal<string list>([ "c1" ], Study.upcoming (script ()) plan progress (Map [ "k2", concept "k2" "A" (graduated t0) ]) 1)

[<Fact>]
let ``the time left counts the paper still to hear, ideas to learn, quick checks and recaps`` () =
    let plan = plan ()
    // 3 ideas to learn, 2 recaps (and a few words of narration)
    Assert.Equal(10, Study.minutesLeft t0 (script ()) plan (Study.Progress.empty t0) Map.empty)

// ---- what the tutor says, and what is heard

[<Fact>]
let ``the tutor says a lesson in short clips, math in words, and the example after it`` () =
    let plan = plan ()
    let lesson = Study.parseLesson (script ()) "p" "c3" lessonText
    let said = Study.lessonSaid plan.Ideas.[2] lesson
    Assert.Equal("Now: Scaled dot-product attention.", said.Head.Say)
    Assert.Contains(said, fun x -> x.Show = "Here's an example.")
    // what is shown keeps its LaTeX; what is said doesn't
    let math = said |> List.find (fun x -> x.Show.Contains "softmax")
    Assert.Contains("\\sqrt", math.Show)
    Assert.DoesNotContain("\\", math.Say)
    Assert.DoesNotContain("$", math.Say)
    // the first clip is short, so speech starts quickly
    let long = String.replicate 12 "This is a sentence of the lesson. "
    let clips = Study.said long
    Assert.True(clips.Head.Show.Length <= 140)
    Assert.True(clips.Length > 1)

[<Fact>]
let ``a question is read with its options in the order shown, and feedback says why`` () =
    let q = (Study.parseLesson (script ()) "p" "c3" lessonText).Checks.[0]
    // shown as: 1 = option 2, 2 = option 1 (right), 3 = option 0, 4 = option 3
    let order = [ 2; 1; 0; 3 ]
    let said = Study.questionSaid q order
    Assert.StartsWith("Option 1: To speed it up. Option 2: To keep large dot products", (List.last said).Say)
    let right = Study.feedbackSaid q order (Some 1) |> List.map (fun x -> x.Say) |> String.concat " "
    // "Right: large scores..." loses its own "Right:"
    Assert.StartsWith("Right. Large scores give tiny gradients", right)
    let wrong = Study.feedbackSaid q order (Some 0) |> List.map (fun x -> x.Say) |> String.concat " "
    Assert.StartsWith("Not quite. The softmax always sums to one. The answer is option 2: To keep large dot products", wrong)
    let unknown = Study.feedbackSaid q order None |> List.map (fun x -> x.Say) |> String.concat " "
    Assert.StartsWith("The answer is option 2", unknown)

[<Fact>]
let ``a spoken answer is a number, the words of an option, or I don't know`` () =
    let options = [ "A matrix of dot products between queries and keys"; "The gradients of the loss"; "The positional encodings" ]
    let heard = Study.heardAnswer options
    Assert.Equal(Study.Heard.Option 1, heard "Two.")
    Assert.Equal(Study.Heard.Option 1, heard "Option two.")
    Assert.Equal(Study.Heard.Option 1, heard "Option 2")
    Assert.Equal(Study.Heard.Option 1, heard "To.")
    Assert.Equal(Study.Heard.Option 1, heard "I'd go for two.")
    Assert.Equal(Study.Heard.Option 1, heard "I'll go with the second one")
    Assert.Equal(Study.Heard.Option 2, heard "Number three")
    Assert.Equal(Study.Heard.Option 1, heard "kakkonen")
    Assert.Equal(Study.Heard.Option 0, heard "One")
    // letters still work when they come through
    Assert.Equal(Study.Heard.Option 1, heard "B.")
    Assert.Equal(Study.Heard.Option 1, heard "Bee")
    Assert.Equal(Study.Heard.Option 0, heard "I think it's A")
    Assert.Equal(Study.Heard.Option 2, heard "Option C, please")
    Assert.Equal(Study.Heard.Option 1, heard "The second one.")
    Assert.Equal(Study.Heard.Option 0, heard "Dot products between the queries and the keys")
    Assert.Equal(Study.Heard.Option 2, heard "kolme")
    Assert.Equal(Study.Heard.DontKnow, heard "I don't know.")
    Assert.Equal(Study.Heard.DontKnow, heard "En tiedä")
    Assert.Equal(Study.Heard.Unclear, heard "D")
    Assert.Equal(Study.Heard.Unclear, heard "Four")
    Assert.Equal(Study.Heard.Unclear, heard "Either one or three")
    // "one" in a longer sentence is not an answer
    Assert.Equal(Study.Heard.Unclear, heard "Recurrent networks have to compute one position after another, so training can't be parallelized")
    Assert.Equal(Study.Heard.Unclear, heard "Hmm, maybe something else entirely")
    Assert.Equal(Study.Heard.Unclear, heard "")

// ---- what the learner knows

let private question (id: string) (choice: bool) (asked: DateTime option) =
    { Id = id; Prompt = id; Options = (if choice then [ "a"; "b" ] else []); Correct = (if choice then 0 else -1); Why = []; Answer = (if choice then "" else "x")
      PaperId = "p"; Visual = None; LastAsked = asked }

[<Fact>]
let ``each review asks the idea another way: the other kind of question, then the one asked longest ago`` () =
    let c = { concept "k" "X" (graduated t0) with Questions = [ question "mc1" true (Some(t0.AddDays 3.0)); question "mc2" true None; question "recall" false (Some t0) ] }
    // multiple choice was asked last: recall next
    Assert.Equal(Some "recall", Knowledge.pick false Set.empty c |> Option.map (fun q -> q.Id))
    // in a session only multiple choice: the one never asked
    Assert.Equal(Some "mc2", Knowledge.pick true Set.empty c |> Option.map (fun q -> q.Id))
    Assert.Equal(Some "mc1", Knowledge.pick true (set [ "mc2" ]) c |> Option.map (fun q -> q.Id))
    let answered = Knowledge.answer 0.9 (t0.AddDays 5.0) Fsrs.Rating.Good "recall" c
    Assert.Equal(Some(t0.AddDays 5.0), (answered.Questions |> List.find (fun q -> q.Id = "recall")).LastAsked)
    Assert.Equal(Some "mc2", Knowledge.pick false Set.empty answered |> Option.map (fun q -> q.Id))

[<Fact>]
let ``a retry right after the explanation is practice: the idea still comes back minutes later`` () =
    let c = { concept "k" "X" (Fsrs.fresh t0) with Questions = [ question "mc1" true None; question "mc2" true None ] }
    let wrong = Knowledge.answer 0.9 t0 Fsrs.Rating.Again "mc1" c
    let retried = Knowledge.asked (t0.AddSeconds 30.0) "mc2" wrong
    Assert.Equal(wrong.Memory, retried.Memory)
    Assert.True(retried.Memory.Due < t0.AddHours 1.0)
    Assert.Equal(Some(t0.AddSeconds 30.0), (retried.Questions |> List.find (fun q -> q.Id = "mc2")).LastAsked)

[<Fact>]
let ``an idea keeps its newest questions from every paper, and where it was met`` () =
    let many = [ for i in 1 .. 20 -> question (sprintf "q%d" i) true None ]
    let c = Knowledge.create t0 "X" "d" false { PaperId = "a"; Title = "A"; Segment = 3 } many
    Assert.Equal(Knowledge.maxQuestions, c.Questions.Length)
    Assert.Equal("q20", (List.last c.Questions).Id)
    Assert.Equal(CardStage.New, c.Memory.Stage)
    let c = c |> Knowledge.addQuestions [ question "q20" true None ] |> Knowledge.addSource { PaperId = "b"; Title = "B"; Segment = 0 }
    Assert.Equal(Knowledge.maxQuestions, c.Questions.Length)
    Assert.Equal<string list>([ "b"; "a" ], c.Sources |> List.map (fun s -> s.PaperId))
    Assert.Equal(Fsrs.Rating.Good, Knowledge.rating (question "q" true None) (Some 0))
    Assert.Equal(Fsrs.Rating.Again, Knowledge.rating (question "q" true None) (Some 1))
    Assert.Equal(Fsrs.Rating.Again, Knowledge.rating (question "q" true None) None)

[<Fact>]
let ``reviews mix ideas and cards: steps first, then what is most forgotten`` () =
    let now = t0.AddDays 30.0
    let review days stability = { Fsrs.fresh t0 with Stage = CardStage.Review; Stability = stability; LastReview = Some(now.AddDays(-days)); Due = now.AddDays(-1.0) }
    let card (front: string) (m: Memory) =
        { Id = front; Front = front; Back = "b"; Visual = None; VisualOnFront = false; Segment = 0; Origin = "paper"; CreatedUtc = t0; Memory = m }
    let idea (name: string) (m: Memory) = { concept name name m with Questions = [ question (name + "q") true None ] }
    let decks = [ "p", [ card "card late" (review 20.0 10.0); card "card slightly late" (review 11.0 10.0) ] ]
    let concepts =
        [ idea "idea very late" (review 40.0 10.0)
          idea "idea step" { Fsrs.fresh t0 with Stage = CardStage.Learning; Due = now.AddMinutes -1.0; LastReview = Some now }
          idea "idea not due" { review 1.0 10.0 with Due = now.AddDays 3.0 }
          // an idea with no question can't be asked
          { idea "idea without questions" (review 40.0 10.0) with Questions = [] } ]
    let names =
        Knowledge.queue now decks concepts
        |> List.map (function
            | Knowledge.ReviewItem.Card (_, c) -> c.Front
            | Knowledge.ReviewItem.Concept (c, _) -> c.Name)
    Assert.Equal<string list>([ "idea step"; "idea very late"; "card late"; "card slightly late" ], names)

// ---- saving

[<Fact>]
let ``what the learner knows and how far a paper's study is are saved and read back`` () =
    let dir = Path.Combine(Path.GetTempPath(), "pr-test-" + Guid.NewGuid().ToString("N"))
    let p = Store.Paths dir
    let c =
        { concept "k1" "Softmax $\\sigma$" (graduated t0) with
            Background = true
            Sources = [ { PaperId = "abc"; Title = "Attention"; Segment = 4 } ]
            Questions =
              [ { question "q1" true (Some t0) with Why = [ "yes"; "no" ]; Visual = Some "E1" }
                question "q2" false None ] }
    Store.saveConcepts p [ c ]
    Assert.Equal<Concept list>([ c ], Store.loadConcepts p)
    let progress: Study.Progress =
        { Links = Map [ "c1", "k1" ]
          Done = Map [ "c1", Study.Outcome.Refreshed; "c2", Study.Outcome.TestedOut ]
          Recaps = set [ 0 ]
          Teach = set [ "c3" ]
          Reported = set [ "abc:c2:1" ]
          Matched = t0
          Heard = 42 }
    Study.saveProgress p "abc" progress
    Assert.Equal(Some progress, Study.loadProgress p "abc")
    Study.savePlan p "abc" planText
    Assert.Equal(3, (Study.loadPlan p "abc" (script ())).Value.Ideas.Length)
    Directory.Delete(dir, true)
