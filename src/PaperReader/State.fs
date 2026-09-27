/// Elmish state: model, messages, update and subscriptions.
module PaperReader.State

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Avalonia.Threading
open Elmish
open PaperReader.Core

type ImportState =
    { Id: Guid
      Name: string
      Step: string
      Progress: float option
      Cancel: CancellationTokenSource }

[<RequireQualifiedAccess>]
type Mic =
    | Idle
    | Recording
    | Transcribing

/// A question on its way: the answer so far streams into Partial.
type PendingAnswer = { Id: Guid; Question: string; Partial: string; ByVoice: bool }

/// The Ask panel: questions about the paper while listening.
type HelpState =
    { /// Segment the questions are about (where the listener paused).
      Position: int
      /// The equation or figure the questions are about, if one was on screen or picked.
      About: string option
      /// Every question asked about this paper, oldest first.
      History: HelpTurn list
      /// How many of History were asked before the panel opened (shown under "Earlier questions").
      Earlier: int
      ShowEarlier: bool
      /// The paper text and figure notes; None while they are prepared.
      Knowledge: Help.Knowledge option
      Preparing: string option
      /// A question tapped while preparing, asked when ready.
      Queued: (Help.Ask * bool) option
      Pending: PendingAnswer option
      Input: string
      Error: string option
      Mic: Mic
      /// The answer (by its time asked) being read aloud.
      Speaking: DateTime option
      /// Its clips made but not played yet, whether one is playing, and whether all are made.
      SpeakQueue: string list
      SpeakPlaying: bool
      SpeakMade: bool
      /// Playback was on when the panel opened: closing it carries on.
      ResumeOnClose: bool
      Cancel: CancellationTokenSource }

/// The Remember panel: making flashcards about the paper while listening.
type CardsPanel =
    { /// Segment the cards are about (where the listener paused).
      Position: int
      /// The equation or figure on screen, if any.
      About: string option
      Input: string
      /// Playback was on when the panel opened: closing it carries on.
      ResumeOnClose: bool }

/// Cards being made by the model.
type MakingCards =
    { Id: Guid
      PaperId: string
      /// What was asked for, as shown.
      What: string
      /// Getting the paper ready, before the model writes.
      Step: string option
      /// Cards written so far.
      Count: int
      Cancel: CancellationTokenSource }

/// A review session: cards and ideas shown one at a time until none is due.
type ReviewState =
    { /// The paper reviewed, or None for every paper.
      Scope: string option
      /// Cards and ideas to show, the current one first.
      Queue: Knowledge.ReviewItem list
      Revealed: bool
      /// The option picked for an idea's multiple-choice question (None: "I don't know"), once answered.
      Choice: int option option
      /// The order its options are shown in, shuffled each time so where the right one sits gives nothing away.
      Order: int list
      /// Answers given, and how many of them were not Again.
      Answered: int
      Remembered: int
      /// Cards in the session when it started (learning steps shown again aren't counted).
      Total: int }

/// Why a question is asked in a study session.
[<RequireQualifiedAccess>]
type QuizPurpose =
    /// Right after the lesson.
    | Check
    /// The lesson was skipped: the question first, the lesson only if the answer is wrong.
    | TestOut
    /// An idea known from another paper that may be fading.
    | QuickCheck
    /// An idea learned minutes ago, asked again between the others.
    | Again

/// A multiple-choice question in a study session.
type Quiz =
    { /// The idea of the plan it is about, if any.
      Idea: string option
      /// The learner's idea, once it exists (the first answer makes it).
      Concept: string option
      Purpose: QuizPurpose
      Question: Question
      /// The options in the order shown and said (indices into the question's), so the right one moves around.
      Order: int list
      /// The option picked; Some None is "I don't know".
      Choice: int option option
      /// The answer was said aloud (and may have been misheard).
      Spoken: bool
      /// Before the answer: the learner's idea (None if the answer made it) and the progress, so a question reported
      /// as wrong can be taken back.
      Before: (Concept option * Study.Progress) option }

/// What a study session is doing.
[<RequireQualifiedAccess>]
type StudyNow =
    /// The tutor is getting ready (planning); the paper is read aloud meanwhile.
    | Starting
    /// The paper is read aloud up to this segment (inclusive); then the tutor speaks.
    | Listening of last: int
    /// The tutor explains an idea.
    | Teach of idea: string
    | Quiz of Quiz
    /// Explaining a part's ideas in one's own words.
    | Recap of part: int
    | Finished

/// What comes when the tutor has said everything.
[<RequireQualifiedAccess>]
type Then =
    /// The lesson's question.
    | Question
    /// Listening for the answer to the question.
    | Answer
    /// Listening for the learner's explanation.
    | Explain
    /// The next step of the session.
    | Next
    /// Nothing: waiting for the learner.
    | Wait

/// The tutor speaking: what it says now, one clip after another.
type Voice =
    { Id: Guid
      Said: Study.Said list
      /// The clip being said, or to be said next, and where in it to start (ms): kept when paused or moved 15 s.
      At: int
      Offset: int
      /// Clips made so far: index -> audio file.
      Ready: Map<int, string>
      /// Saying it, or waiting for the clip to be made.
      Playing: bool
      Then: Then }

/// What the learner is heard saying.
[<RequireQualifiedAccess>]
type Hearing =
    /// The answer to the question on screen.
    | Answer
    /// A part explained in their own words.
    | Explanation
    /// A question to the tutor.
    | Question

/// The microphone in a study session.
[<RequireQualifiedAccess>]
type Ear =
    | Off
    /// The chime, then the microphone opening.
    | Opening of Hearing
    /// Listening: how loud it is now, and whether speech was heard yet.
    | Open of Hearing * level: float * spoke: bool
    | Transcribing of Hearing

/// Studying the open paper with a tutor (see Study): the paper read aloud, and the tutor between its sections.
type StudyState =
    { Plan: Study.Plan option
      /// Writing the plan: what is happening, and the ideas written so far.
      Planning: (string * string list) option
      Progress: Study.Progress
      Lessons: Map<string, Study.Lesson>
      /// Lessons being written: idea -> the text so far.
      Writing: Map<string, string>
      /// Lessons that couldn't be written: idea -> why.
      Failed: Map<string, string>
      Now: StudyNow
      /// The session goes on by itself; false when the learner paused it.
      Running: bool
      Voice: Voice option
      /// What the tutor was saying before answering a question in the tutor panel, to go back to.
      Aside: Voice option
      Ear: Ear
      /// Answers to the question on screen that weren't heard or understood.
      Tries: int
      /// What the learner said last.
      Heard: string
      /// What the conversation with the tutor is about (an idea of the plan, or the learner's idea), and the conversation.
      ChatAbout: string
      Chat: Study.TutorTurn list
      Followups: string list
      /// A question to the tutor on its way: its id, the question, and the answer so far.
      Pending: (Guid * string * string) option
      Input: string
      /// Asking the tutor: the panel with the conversation is open.
      ShowTutor: bool
      /// The plan: every idea, what is known and what is left.
      ShowPlan: bool
      /// A recap's answer: heard or typed, and the tutor's feedback on it.
      RecapInput: string
      Feedback: Study.Feedback option
      Grading: bool
      Error: string option
      /// The learner's idea just asked about, so it isn't asked again straight away.
      Last: string option
      /// Stops the plan, the tutor and feedback when the session closes (lessons are finished and kept).
      Cancel: CancellationTokenSource }

type ReaderState =
    { Paper: PaperInfo
      Script: Script
      /// Clip being listened to, and position inside it.
      Current: int
      Offset: int
      /// The listener wants audio (it may still be waiting for the clip to be synthesized).
      Playing: bool
      Waiting: bool
      /// Increases with every clip started, so late "clip ended" events from older clips are ignored.
      Generation: int
      Durations: Map<int, int>
      VoiceKey: string
      Error: string option
      Finished: bool
      ShowOutline: bool
      /// The equations list is open.
      ShowEquations: bool
      /// The visual shown full screen, if any.
      Zoom: string option
      /// Where "stop at equations" pauses: segment index -> the equation it has just read and explained.
      Stops: Map<int, string>
      /// The equation kept on screen after such a stop, until the listener continues.
      Held: string option
      /// The Ask panel, when open.
      Help: HelpState option
      /// The Remember panel, when open.
      Cards: CardsPanel option
      /// The study session, when open.
      Study: StudyState option }

/// A search on the Find papers screen.
type SearchState =
    { /// Words searched for, as sent.
      Query: string
      Results: Discover.Found list
      /// Matching papers in all, and how many pages are shown.
      Total: int
      Page: int
      Loading: bool
      Error: string option
      /// Identifies the latest request, so answers to older ones are dropped.
      Id: Guid }

[<RequireQualifiedAccess>]
type Recs =
    | NotLoaded
    | Loading of step: string
    /// The recommendations, and the library they were made for (paper ids).
    | Ready of Discover.Found list * seeds: string list
    | Failed of string

/// Find papers: search, recommendations, downloading.
type DiscoverState =
    { Input: string
      /// The search shown; None shows the recommendations.
      Search: SearchState option
      Recs: Recs
      /// The result whose abstract is shown in full.
      Expanded: string option
      /// The result being downloaded, what is happening, and how to stop it.
      Fetching: (string * string * CancellationTokenSource) option
      /// A download that failed: the result's key and why.
      Failed: (string * string) option
      /// Library papers' titles and where they came from, to mark results already in the library.
      Known: (PaperInfo * Discover.Source) list }

[<RequireQualifiedAccess>]
type Screen =
    | Library
    | Discover
    /// Flashcards of every paper: review, and make decks.
    | Learn
    | Importing of ImportState
    | Reader of ReaderState

type Model =
    { Screen: Screen
      Papers: PaperInfo list
      Settings: Settings
      ShowSettings: bool
      Voices: Mistral.Voice list
      VoicesStatus: string option
      Notice: string option
      /// A paper whose remove button was tapped once (a second tap removes it).
      ConfirmDelete: string option
      /// Find papers, kept while the app runs so going back to it shows the same results.
      Discover: DiscoverState
      /// Every paper's flashcards, by paper id.
      Decks: Map<string, Card list>
      Making: MakingCards option
      /// The cards made last (paper id, cards), shown so bad ones can be removed; or why making failed.
      Made: (string * Card list) option
      MakeError: string option
      /// A review session, shown over any screen.
      Review: ReviewState option
      /// The paper whose cards are listed on the Learn screen.
      LearnOpen: string option
      /// What the learner knows across papers (Study), by id.
      Concepts: Map<string, Concept>
      /// A paper opened from the Learn screen to study, not to listen.
      StudyOnOpen: string option
      /// How far each paper's study plan is (ideas done, ideas), for the Learn screen.
      Studied: Map<string, int * int>
      /// The list of what the learner knows is open on the Learn screen.
      ShowKnown: bool
      /// The app's size in dp (0 until it is laid out), for layouts that change with it: the walking player and the
      /// reader's title in landscape, and the full-screen view of an equation.
      Viewport: float * float }

type Msg =
    | OpenDocument
    | DocumentPicked of (string * string) option
    | ImportFile of path: string * name: string
    | ImportProgress of importId: Guid * step: string * progress: float option
    | ImportFinished of importId: Guid * Result<PaperInfo * Script * string option, string>
    | CancelImport
    | OpenPaper of PaperInfo
    | PaperLoaded of Result<PaperInfo * Script * Map<int, int>, string>
    | AskDelete of string
    | DeletePaper of string
    | CloseReader
    | TogglePlay
    /// Play (true) or pause (false) from the notification, a headset or an incoming call.
    | Remote of play: bool
    | Back15
    | Forward15
    | CycleSpeed
    | JumpToSegment of int
    | Tick of positionMs: int
    | ClipEnded of generation: int
    | ClipReady of paperId: string * voiceKey: string * index: int * durationMs: int
    | ClipFailed of paperId: string * index: int * message: string
    | PlayerFailed of string
    | ToggleOutline
    /// Opens the full-screen view of the equation on screen, or closes it.
    | ToggleZoom
    | ZoomVisual of string
    /// Turns wide visuals sideways full screen (true), or keeps them upright.
    | SetTurnSideways of bool
    | ToggleEquations
    | SetStopAtEquations of bool
    | SetStopAtFigures of bool
    /// Switches the reader between the full player and the simple walking one.
    | SetWalking of bool
    | SetShowSettings of bool
    | SetApiKey of string
    | SetNarration of bool
    | SetNarrationModel of string
    | SetMistralVoice of bool
    | UsePhoneVoice
    | PickVoice of Mistral.Voice
    | LoadVoices
    | VoicesLoaded of Result<Mistral.Voice list, string>
    | Dismiss
    | BackPressed
    /// The app was laid out at a new size (dp): rotated, or the window resized.
    | Resized of width: float * height: float
    // ----- Ask
    /// Opens the Ask panel about what is on screen, or about a given equation or figure.
    | OpenHelp of about: string option
    | CloseHelp of resume: bool
    | HelpStep of paperId: string * step: string
    | HelpPrepared of paperId: string * Result<Help.Knowledge, string>
    | AskHelp of Help.Ask * byVoice: bool
    | HelpText of askId: Guid * text: string
    | HelpAnswered of askId: Guid * Result<HelpTurn, string>
    | SetHelpInput of string
    | SendHelpInput
    | ToggleEarlier
    | MicPressed
    | MicStarted of Result<unit, string>
    | Transcribed of Result<string, string>
    | SpeakAnswer of HelpTurn
    | AnswerAudio of asked: DateTime * Result<string * bool, string>
    | AnswerSpoken of asked: DateTime
    | StopSpeaking
    | SetAboutMe of string
    | SetHelpModel of string
    // ----- Find papers
    | OpenDiscover
    | CloseDiscover
    | SetDiscoverInput of string
    | RunSearch
    /// Searches for a suggested topic.
    | SearchTopic of string
    | SearchMore
    | SearchDone of searchId: Guid * Result<Discover.Results, string>
    | ClearSearch
    | LoadRecs of force: bool
    | RecsStep of string
    | RecsDone of seeds: string list * Result<Discover.Found list, string>
    | ToggleAbstract of key: string
    | FetchPaper of Discover.Found
    | FetchStep of key: string * step: string
    | FetchDone of key: string * Result<string * string, string>
    | CancelFetch
    /// Opens a web page in the system browser.
    | OpenLink of string
    | SetOpenAlexKey of string
    // ----- Learn
    | OpenLearn
    | CloseLearn
    /// Shows or hides a paper's cards on the Learn screen.
    | ToggleDeck of paperId: string
    /// Opens the Remember panel about what is on screen (or the given equation or figure).
    | OpenCards of about: string option
    | CloseCards of resume: bool
    | SetCardInput of string
    | SendCardInput
    | MakeCards of paperId: string * Cards.Request
    | CardsStep of makeId: Guid * step: string
    | CardsProgress of makeId: Guid * count: int
    | CardsMade of makeId: Guid * Result<Card list, string>
    | CancelMaking
    | DeleteCard of paperId: string * cardId: string
    | StartReview of paperId: string option
    | ShowAnswer
    | RateCard of Fsrs.Rating
    /// Space or Enter in a review: shows the answer, or answers Good.
    | ReviewNext
    | CloseReview
    /// Listens to where the card's subject is in the paper.
    | ListenToCard of paperId: string * segment: int
    | SetRetention of float
    /// Picks an option of an idea's multiple-choice question in a review (None: "I don't know").
    | ReviewChoose of int option
    /// The keys 1 to 4 in a review: an option before a question is answered, a rating after.
    | ReviewKey of int
    | ToggleKnown
    /// Removes an idea from what the learner knows.
    | ForgetConcept of string
    // ----- Study
    /// Turns the tutor on for the open paper: the study session starts, or goes on where it was.
    | OpenStudy
    /// Opens a paper to study it.
    | StudyPaper of PaperInfo
    /// Turns the tutor off: the paper is only read aloud.
    | CloseStudy
    | StudyStep of paperId: string * step: string
    | StudyPlanText of paperId: string * text: string
    | StudyPlanned of paperId: string * Result<string, string>
    | RetryPlan
    | StudyLessonText of paperId: string * idea: string * text: string
    | StudyLessonDone of paperId: string * idea: string * Result<string, string>
    | StudyRetryLesson of idea: string
    | StudyMatched of paperId: string * Map<string, string>
    | ToggleStudyPlan
    /// Teaches an idea picked in the plan (again, or although it is known).
    | StudyTeach of idea: string
    /// Skips the rest of the lesson: its question first, the lesson only if the answer is wrong.
    | StudyTestOut
    | StudyChoose of int option
    /// The answer as heard (by the microphone).
    | StudySaid of int option
    /// The answer was misheard: take it back and listen again.
    | StudyMisheard
    /// The question on screen is wrong: take the answer back and never ask it again.
    | StudyReport
    /// Says it again: the question, or back a sentence.
    | StudyAgain
    /// Skips ahead: the lesson to its question, a question as not known, the feedback, or the recap.
    | StudySkip
    /// Keys in a study session: 1 to 4 pick an option.
    | StudyKey of int
    /// The microphone button in the tutor's panel: listens for a question, or ends listening now.
    | StudyMic
    | StudyClipReady of voice: Guid * index: int * path: string
    | StudyClipFailed of voice: Guid * index: int * message: string
    | StudyClipEnded of voice: Guid * index: int
    /// The chime, then the microphone opens.
    | StudyListen of Hearing
    | StudyEarOpened of Hearing
    | StudyEarLevel of float
    | StudyEarTranscribing of Hearing
    | StudyHeard of Hearing * Result<string, string>
    /// Opens or closes the conversation with the tutor.
    | ToggleTutor
    | StudyAsk of question: string * quick: bool
    | StudyAskText of askId: Guid * text: string
    | StudyAnswered of askId: Guid * Result<Help.Reply, string>
    | SetStudyInput of string
    | SendStudyInput
    | SetRecapInput of string
    | SubmitRecap
    | RecapText of Study.Feedback
    | RecapDone of Result<Study.Feedback, string>
    | SetStudy of bool
    | SetAnswerAloud of bool

let speeds = [| 0.8; 1.0; 1.15; 1.3; 1.5; 1.75; 2.0 |]
let jumpMs = 15000

// ---------------------------------------------------------------------------------------------
// Side effects
// ---------------------------------------------------------------------------------------------

let private platform () = Services.get ()
let private paths () = Store.Paths((platform ()).DataDir)

/// The synthesizer for the open paper (one at a time).
let mutable private synth: Synth.Synthesizer option = None

let private stopSynth () =
    synth |> Option.iter (fun s -> (s :> IDisposable).Dispose())
    synth <- None

let private engineFor (settings: Settings) : Synth.ISpeechEngine option =
    if Settings.hasKey settings && settings.UseMistralVoice then
        Some(Synth.MistralEngine(settings.MistralApiKey, settings.VoiceId))
    else (platform ()).SystemSpeech

let private startSynth (r: ReaderState) (settings: Settings) : Cmd<Msg> =
    Cmd.ofEffect (fun dispatch ->
        stopSynth ()
        match engineFor settings with
        | None -> dispatch (ClipFailed(r.Paper.Id, r.Current, "No offline text-to-speech engine was found. Add a Mistral API key in Settings (or install espeak-ng on Linux)."))
        | Some engine ->
            let p = paths ()
            let paperId = r.Paper.Id
            let key = r.VoiceKey
            let lookahead = if engine.Key = "system" then 12 else 8
            let s =
                new Synth.Synthesizer(
                    r.Script, engine, (fun i -> p.Audio(paperId, key, i)), lookahead,
                    (fun i d -> dispatch (ClipReady(paperId, key, i, d))),
                    (fun i m -> dispatch (ClipFailed(paperId, i, m))))
            s.SetCursor r.Current
            s.Start()
            synth <- Some s)

let private moveSynth (i: int) : Cmd<Msg> =
    Cmd.ofEffect (fun _ -> synth |> Option.iter (fun s -> s.SetCursor i))

let private stopAudio: Cmd<Msg> =
    Cmd.ofEffect (fun _ -> (platform ()).Player.Stop())

/// Screen and media notification follow playback: on while reading aloud, with the paper and section shown.
let private playbackState (r: ReaderState) (on: bool) : Cmd<Msg> =
    Cmd.ofEffect (fun _ ->
        let p = platform ()
        p.KeepScreenOn on
        let section = r.Script.Segments.[r.Current].Section
        let detail =
            if section > 0 && section < r.Script.Sections.Length then r.Script.Sections.[section].Title else "Beginning"
        p.SetPlayback(r.Script.Title, detail, on))

let private saveProgress (r: ReaderState) : Cmd<Msg> =
    Cmd.ofEffect (fun _ ->
        try Store.saveMeta (paths ()) { r.Paper with LastSegment = r.Current; SegmentCount = r.Script.Segments.Length }
        with _ -> ())

let private saveSettings (s: Settings) : Cmd<Msg> =
    Cmd.ofEffect (fun _ -> try Store.saveSettings (paths ()) s with _ -> ())

let private loadLibrary () =
    try Store.library (paths ()) with _ -> []

let private loadDecks (papers: PaperInfo list) : Map<string, Card list> =
    let p = paths ()
    papers
    |> List.map (fun x -> x.Id, (try Store.loadCards p x.Id with _ -> []))
    |> List.filter (fun (_, cards) -> not cards.IsEmpty)
    |> Map.ofList

let private runTask (work: unit -> Task<'a>) (ok: 'a -> Msg) (fail: exn -> Msg) : Cmd<Msg> =
    Cmd.ofEffect (fun dispatch ->
        Task.Run(fun () ->
            task {
                try
                    let! r = work ()
                    dispatch (ok r)
                with e ->
                    let e = match e with :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException | e -> e
                    dispatch (fail e)
            }
            :> Task)
        |> ignore)

let private errorText (e: exn) =
    match e with
    | :? OperationCanceledException -> "Cancelled."
    | Mistral.MistralError(_, m) -> m
    | Discover.DiscoverError m -> m
    | :? Net.Http.HttpRequestException -> "Couldn't reach Mistral. Check the internet connection."
    | e -> e.Message

// ---------------------------------------------------------------------------------------------
// Playback
// ---------------------------------------------------------------------------------------------

/// Starts (or waits for) the clip at the reader's current position.
let private play (r: ReaderState) (settings: Settings) : ReaderState * Cmd<Msg> =
    let gen = r.Generation + 1
    let r = { r with Playing = true; Finished = false; Generation = gen; Held = None }
    let path = (paths ()).Audio(r.Paper.Id, r.VoiceKey, r.Current)
    // a clip cached on disk but not yet known here (the synthesizer only reports clips it makes)
    let r =
        if r.Durations.ContainsKey r.Current || not (File.Exists path) then r
        else
            match (try Wav.durationMs path with _ -> 0) with
            | d when d > 0 -> { r with Durations = r.Durations.Add(r.Current, d) }
            | _ -> r
    if r.Durations.ContainsKey r.Current then
        let start =
            Cmd.ofEffect (fun dispatch ->
                (platform ()).Player.Play(path, r.Offset, settings.Speed, (fun () -> dispatch (ClipEnded gen)), (fun e -> dispatch (PlayerFailed e))))
        { r with Waiting = false }, Cmd.batch [ start; moveSynth r.Current; playbackState r true ]
    else
        { r with Waiting = true }, Cmd.batch [ stopAudio; moveSynth r.Current; playbackState r true ]

let private pause (r: ReaderState) : ReaderState * Cmd<Msg> =
    let offset = if r.Playing && not r.Waiting then (platform ()).Player.PositionMs else r.Offset
    { r with Playing = false; Waiting = false; Offset = offset; Generation = r.Generation + 1 },
    Cmd.batch [ stopAudio; playbackState r false; saveProgress r ]

/// Moves to a position, keeping the play/pause state.
let private seek (r: ReaderState) (settings: Settings) (pos: Timeline.Position) : ReaderState * Cmd<Msg> =
    let r = { r with Current = pos.Segment; Offset = pos.OffsetMs; Finished = false; Error = None; Held = None }
    if r.Playing then play r settings
    else r, Cmd.batch [ moveSynth r.Current; saveProgress r ]

let private currentPosition (r: ReaderState) : Timeline.Position =
    let offset = if r.Playing && not r.Waiting then (platform ()).Player.PositionMs else r.Offset
    { Segment = r.Current; OffsetMs = offset }

// ---------------------------------------------------------------------------------------------
// Init and update
// ---------------------------------------------------------------------------------------------

/// True when the clip that just ended finishes explaining an equation or figure the listener wants to stop at.
let private stopsHere (settings: Settings) (r: ReaderState) =
    match r.Stops.TryFind r.Current |> Option.bind r.Script.Visual with
    | Some v ->
        match v.Kind with
        | VisualKind.Figure | VisualKind.Table -> settings.StopAtFigures
        | _ -> settings.StopAtEquations
    | None -> false

let init () : Model * Cmd<Msg> =
    let settings = try Store.loadSettings (paths ()) with _ -> Settings.defaults
    let papers = loadLibrary ()
    { Screen = Screen.Library
      Papers = papers
      Settings = settings
      ShowSettings = false
      Voices = []
      VoicesStatus = None
      Notice = None
      ConfirmDelete = None
      Discover =
        { Input = ""
          Search = None
          Recs = Recs.NotLoaded
          Expanded = None
          Fetching = None
          Failed = None
          Known = [] }
      Decks = loadDecks papers
      Making = None
      Made = None
      MakeError = None
      Review = None
      LearnOpen = None
      Concepts = (try Store.loadConcepts (paths ()) with _ -> []) |> List.map (fun c -> c.Id, c) |> Map.ofList
      StudyOnOpen = None
      Studied = Map.empty
      ShowKnown = false
      Viewport = (0.0, 0.0) },
    Cmd.ofEffect (fun dispatch ->
        (platform ()).SetIncomingFileHandler(fun (path, name) -> dispatch (ImportFile(path, name)))
        (platform ()).SetRemoteHandler(fun play -> dispatch (Remote play)))

let private withReader (model: Model) (f: ReaderState -> ReaderState * Cmd<Msg>) : Model * Cmd<Msg> =
    match model.Screen with
    | Screen.Reader r ->
        let r, cmd = f r
        { model with Screen = Screen.Reader r }, cmd
    | _ -> model, Cmd.none

let private pickDocument () : Task<(string * string) option> =
    task {
        match Services.topLevel with
        | None -> return None
        | Some top ->
            let options =
                Avalonia.Platform.Storage.FilePickerOpenOptions(
                    Title = "Open a document",
                    AllowMultiple = false,
                    FileTypeFilter =
                        [| Avalonia.Platform.Storage.FilePickerFileType("Documents", Patterns = Formats.patterns, MimeTypes = Formats.mimeTypes)
                           Avalonia.Platform.Storage.FilePickerFileType("All files", Patterns = [| "*" |]) |])
            let! files = Dispatcher.UIThread.InvokeAsync<Collections.Generic.IReadOnlyList<Avalonia.Platform.Storage.IStorageFile>>(fun () -> top.StorageProvider.OpenFilePickerAsync options)
            if files.Count = 0 then return None
            else
                let file = files.[0]
                let incoming = Path.Combine((platform ()).DataDir, "incoming")
                Directory.CreateDirectory incoming |> ignore
                // the extension is kept: it helps tell the format
                let dest = Path.Combine(incoming, Guid.NewGuid().ToString("N") + Path.GetExtension(file.Name).ToLowerInvariant())
                use! input = file.OpenReadAsync()
                use output = File.Create dest
                do! input.CopyToAsync output
                return Some(dest, file.Name)
    }

// ---------------------------------------------------------------------------------------------
// Ask
// ---------------------------------------------------------------------------------------------

/// Paper text and figure notes, kept while the app runs (they are also cached on disk).
let private knowledge = Collections.Concurrent.ConcurrentDictionary<string, Help.Knowledge>()

let private prepareHelp (settings: Settings) (r: ReaderState) : Cmd<Msg> =
    let id = r.Paper.Id
    let p = paths ()
    Cmd.ofEffect (fun dispatch ->
        Task.Run(fun () ->
            task {
                try
                    let! k = Help.prepare settings p id r.Script (fun step -> dispatch (HelpStep(id, step))) CancellationToken.None
                    knowledge.[id] <- k
                    dispatch (HelpPrepared(id, Ok k))
                with e ->
                    let e = match e with :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException | e -> e
                    dispatch (HelpPrepared(id, Error(errorText e)))
            }
            :> Task)
        |> ignore)

let private startAsk (settings: Settings) (r: ReaderState) (h: HelpState) (k: Help.Knowledge) (ask: Help.Ask) (byVoice: bool) : HelpState * Cmd<Msg> =
    let about = h.About |> Option.bind r.Script.Visual
    let askId = Guid.NewGuid()
    let pending = { Id = askId; Question = Help.questionText ask about; Partial = ""; ByVoice = byVoice }
    let ct = h.Cancel.Token
    let history = h.History
    let work =
        Cmd.ofEffect (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! turn = Help.ask settings r.Script k history h.Position about ask (fun t -> dispatch (HelpText(askId, t))) ct
                        dispatch (HelpAnswered(askId, Ok turn))
                    with e ->
                        let e = match e with :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException | e -> e
                        dispatch (HelpAnswered(askId, Error(errorText e)))
                }
                :> Task)
            |> ignore)
    { h with Pending = Some pending; Error = None; Queued = None; Input = (match ask with Help.Ask.Free _ -> "" | _ -> h.Input) }, work

/// Synthesis of the answer being read aloud.
let mutable private speakCancel = new CancellationTokenSource()

let private stopAnswerAudio (h: HelpState) : Cmd<Msg> =
    speakCancel.Cancel()
    if h.Speaking.IsSome then stopAudio else Cmd.none

let private notSpeaking (h: HelpState) = { h with Speaking = None; SpeakQueue = []; SpeakPlaying = false; SpeakMade = false }

let private playAnswerClip (speed: float) (asked: DateTime) (path: string) : Cmd<Msg> =
    Cmd.ofEffect (fun dispatch ->
        (platform ()).Player.Play(path, 0, speed, (fun () -> dispatch (AnswerSpoken asked)), (fun _ -> dispatch (AnswerSpoken asked))))

let private withHelp (model: Model) (f: ReaderState -> HelpState -> HelpState * Cmd<Msg>) : Model * Cmd<Msg> =
    match model.Screen with
    | Screen.Reader ({ Help = Some h } as r) ->
        let h, cmd = f r h
        { model with Screen = Screen.Reader { r with Help = Some h } }, cmd
    | _ -> model, Cmd.none

// ---------------------------------------------------------------------------------------------
// Learn
// ---------------------------------------------------------------------------------------------

let deckOf (model: Model) (paperId: string) = model.Decks.TryFind paperId |> Option.defaultValue []

/// Replaces a paper's cards and saves them.
let private setDeck (model: Model) (paperId: string) (cards: Card list) : Model * Cmd<Msg> =
    { model with Decks = (if cards.IsEmpty then model.Decks.Remove paperId else model.Decks.Add(paperId, cards)) },
    Cmd.ofEffect (fun _ -> try Store.saveCards (paths ()) paperId cards with _ -> ())

/// Where cards about "this" are: the moment the Ask or Remember panel opened at, or where playback is.
let private cardPosition (r: ReaderState) =
    match r.Help, r.Cards with
    | Some h, _ -> h.Position
    | None, Some c -> c.Position
    | None, None -> if r.Held.IsSome && r.Current > 0 then r.Current - 1 else r.Current

let private startMaking (settings: Settings) (paperId: string) (script: Script option) (position: int) (existing: Card list)
                        (request: Cards.Request) : MakingCards * Cmd<Msg> =
    let id = Guid.NewGuid()
    let cts = new CancellationTokenSource()
    let p = paths ()
    let work =
        Cmd.ofEffect (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let script =
                            match script |> Option.orElse (Store.loadScript p paperId) with
                            | Some s -> s
                            | None -> failwith "This paper's cache is from an older version. Remove it and add the document again."
                        let! k =
                            match knowledge.TryGetValue paperId with
                            | true, k -> Task.FromResult k
                            | _ ->
                                task {
                                    let! k = Help.prepare settings p paperId script (fun step -> dispatch (CardsStep(id, step))) cts.Token
                                    knowledge.[paperId] <- k
                                    return k
                                }
                        dispatch (CardsProgress(id, 0))
                        let! cards = Cards.make settings script k position existing request (fun n -> dispatch (CardsProgress(id, n))) cts.Token
                        dispatch (CardsMade(id, Ok cards))
                    with e ->
                        let e = match e with :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException | e -> e
                        dispatch (CardsMade(id, Error(errorText e)))
                }
                :> Task)
            |> ignore)
    let what = match script with Some s -> Cards.describe s request | None -> "The whole paper"
    { Id = id; PaperId = paperId; What = what; Step = Some "Getting ready"; Count = 0; Cancel = cts }, work

let private withReview (model: Model) (f: ReviewState -> ReviewState * Cmd<Msg>) : Model * Cmd<Msg> =
    match model.Review with
    | Some rv ->
        let rv, cmd = f rv
        { model with Review = Some rv }, cmd
    | None -> model, Cmd.none

let private saveConcepts (concepts: Map<string, Concept>) : Cmd<Msg> =
    let all = concepts |> Map.toList |> List.map snd |> List.sortBy (fun c -> c.CreatedUtc)
    Cmd.ofEffect (fun _ -> try Store.saveConcepts (paths ()) all with _ -> ())

/// Everything due now: flashcards and ideas, of one paper or all.
let reviewQueue (model: Model) (scope: string option) (now: DateTime) =
    let decks = match scope with Some id -> [ id, deckOf model id ] | None -> Map.toList model.Decks
    let concepts = model.Concepts |> Map.toList |> List.map snd
    let concepts = match scope with Some id -> Knowledge.ofPaper id concepts | None -> concepts
    Knowledge.queue now decks concepts

/// A review with its first item ready: nothing picked or shown yet.
let private shuffled (queue: Knowledge.ReviewItem list) =
    match queue with
    | Knowledge.ReviewItem.Concept (_, q) :: _ -> [ 0 .. q.Options.Length - 1 ] |> List.sortBy (fun _ -> Random.Shared.Next())
    | _ -> []

let private reviewAt (rv: ReviewState) (queue: Knowledge.ReviewItem list) =
    { rv with Queue = queue; Revealed = false; Choice = None; Order = shuffled queue }

// ---------------------------------------------------------------------------------------------
// Study
// ---------------------------------------------------------------------------------------------

let private (|Studying|_|) (model: Model) =
    match model.Screen with
    | Screen.Reader ({ Study = Some s } as r) -> Some(r, s)
    | _ -> None

/// The tutor has the turn (not the paper being read).
let tutorTurn (s: StudyState) =
    match s.Now with
    | StudyNow.Listening _
    | StudyNow.Starting -> false
    | _ -> true

let private setStudy (model: Model) (r: ReaderState) (s: StudyState) =
    { model with Screen = Screen.Reader { r with Study = Some s } }

let private inner (e: exn) =
    match e with
    | :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException
    | e -> e

let private background (work: (Msg -> unit) -> Task) : Cmd<Msg> =
    Cmd.ofEffect (fun dispatch -> Task.Run(fun () -> work dispatch) |> ignore)

/// Paper text and figure notes being prepared, shared by the requests that need them at the same time.
let private preparing = Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<Help.Knowledge>>>()

let private knowledgeFor (settings: Settings) (paperId: string) (script: Script) (step: string -> unit) : Task<Help.Knowledge> =
    match knowledge.TryGetValue paperId with
    | true, k -> Task.FromResult k
    | _ ->
        let job =
            preparing.GetOrAdd(
                paperId,
                fun _ ->
                    lazy
                        (task {
                            try
                                let! k = Help.prepare settings (paths ()) paperId script step CancellationToken.None
                                knowledge.[paperId] <- k
                                return k
                            finally
                                preparing.TryRemove paperId |> ignore
                        }))
        job.Value

let private saveStudy (paperId: string) (progress: Study.Progress) : Cmd<Msg> =
    Cmd.ofEffect (fun _ -> try Study.saveProgress (paths ()) paperId progress with _ -> ())

let private writePlan (settings: Settings) (r: ReaderState) (concepts: Concept list) (ct: CancellationToken) : Cmd<Msg> =
    let id = r.Paper.Id
    let script = r.Script
    background (fun dispatch ->
        task {
            try
                let! k = knowledgeFor settings id script (fun step -> dispatch (StudyStep(id, step)))
                dispatch (StudyStep(id, "Planning the ideas to learn"))
                let! text = Study.makePlan settings script k concepts (fun t -> dispatch (StudyPlanText(id, t))) ct
                Study.savePlan (paths ()) id text
                dispatch (StudyPlanned(id, Ok text))
            with e -> dispatch (StudyPlanned(id, Error(errorText (inner e))))
        })

/// Writes a lesson and keeps it, even if the session closes meanwhile.
let private writeLesson (settings: Settings) (r: ReaderState) (plan: Study.Plan) (progress: Study.Progress) (concepts: Map<string, Concept>) (idea: Study.Idea) : Cmd<Msg> =
    let id = r.Paper.Id
    let script = r.Script
    background (fun dispatch ->
        task {
            try
                let! k = knowledgeFor settings id script ignore
                let! text = Study.makeLesson settings script k plan progress concepts idea (fun t -> dispatch (StudyLessonText(id, idea.Id, t))) CancellationToken.None
                Study.saveLesson (paths ()) id idea.Id text
                dispatch (StudyLessonDone(id, idea.Id, Ok text))
            with e -> dispatch (StudyLessonDone(id, idea.Id, Error(errorText (inner e))))
        })

/// Matches the plan against ideas learned in other papers since it was last matched, so they can be skipped too.
let private rematch (settings: Settings) (r: ReaderState) (plan: Study.Plan) (progress: Study.Progress) (concepts: Map<string, Concept>) : Cmd<Msg> =
    let id = r.Paper.Id
    let others = concepts |> Map.toList |> List.map snd |> List.filter (fun c -> not (c.Sources |> List.exists (fun x -> x.PaperId = id)))
    if not (Settings.hasKey settings) || not (others |> List.exists (fun c -> c.CreatedUtc > progress.Matched)) then Cmd.none
    else
        background (fun dispatch ->
            task {
                try
                    let! found = Study.matchLearned settings r.Script.Title plan progress others CancellationToken.None
                    dispatch (StudyMatched(id, found))
                with _ -> () // tried again next time
            })

/// Starts writing the lessons of the idea being taught and the next ones, two at a time.
let private prefetch (settings: Settings) (r: ReaderState) (s: StudyState) (concepts: Map<string, Concept>) : StudyState * Cmd<Msg> =
    match s.Plan with
    | Some plan when Settings.hasKey settings ->
        let current = match s.Now with StudyNow.Teach i -> [ i ] | _ -> []
        let start =
            current @ Study.upcoming r.Script plan s.Progress concepts 3
            |> List.distinct
            |> List.filter (fun i -> not (s.Lessons.ContainsKey i || s.Writing.ContainsKey i || s.Failed.ContainsKey i))
            |> List.truncate (max 0 (2 - s.Writing.Count))
            |> List.choose plan.Idea
        { s with Writing = start |> List.fold (fun w i -> w.Add(i.Id, "")) s.Writing },
        Cmd.batch [ for i in start -> writeLesson settings r plan s.Progress concepts i ]
    | _ -> s, Cmd.none

// ----- the tutor's voice

/// Making the clips of what the tutor is saying; cancelled when it says something else.
let mutable private voiceCancel = new CancellationTokenSource()

let private clipPath (settings: Settings) (paperId: string) (say: string) =
    (paths ()).StudyAudio(paperId, Settings.voiceKey settings, say)

/// Makes the clips not made yet, two at a time in order, and tells `voice` (if any) as each is ready.
let private makeClips (settings: Settings) (paperId: string) (voice: Guid option) (said: Study.Said list) (ct: CancellationToken) : Cmd<Msg> =
    match engineFor settings with
    | None -> Cmd.none
    | Some engine ->
        background (fun dispatch ->
            task {
                use gate = new SemaphoreSlim(2)
                let one (i: int) (x: Study.Said) : Task =
                    task {
                        let path = clipPath settings paperId x.Say
                        if not (File.Exists path) then
                            do! gate.WaitAsync(ct)
                            try
                                try
                                    let! wav = engine.Synthesize(x.Say, ct)
                                    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                                    Wav.normalizeTo wav 250 path |> ignore
                                    voice |> Option.iter (fun v -> dispatch (StudyClipReady(v, i, path)))
                                with
                                | :? OperationCanceledException -> ()
                                | e -> voice |> Option.iter (fun v -> dispatch (StudyClipFailed(v, i, errorText (inner e))))
                            finally
                                gate.Release() |> ignore
                    }
                try do! Task.WhenAll(said |> List.mapi one)
                with _ -> ()
            })

/// The screen stays on and the media notification shows the session while the tutor speaks.
let private tutorPlayback (r: ReaderState) (s: StudyState) (on: bool) : Cmd<Msg> =
    Cmd.ofEffect (fun _ ->
        let p = platform ()
        p.KeepScreenOn on
        let detail =
            match s.Now, s.Plan with
            | StudyNow.Teach i, Some plan -> plan.Idea i |> Option.map (fun i -> "Tutor · " + i.Name) |> Option.defaultValue "Tutor"
            | StudyNow.Quiz _, _ -> "Tutor · a question"
            | StudyNow.Recap _, _ -> "Tutor · explain it back"
            | _ -> "Tutor"
        p.SetPlayback(r.Script.Title, detail, on))

let private playClip (settings: Settings) (v: Voice) : Cmd<Msg> =
    match v.Ready.TryFind v.At with
    | Some path ->
        let id, at = v.Id, v.At
        Cmd.ofEffect (fun dispatch ->
            (platform ()).Player.Play(path, v.Offset, settings.Speed, (fun () -> dispatch (StudyClipEnded(id, at))), (fun _ -> dispatch (StudyClipEnded(id, at)))))
    // waiting for it to be made
    | None -> stopAudio

// ----- listening to the learner

/// The microphone's listening; cancelled when the session pauses or moves on.
let mutable private earCancel = new CancellationTokenSource()

let private stopEar () = earCancel.Cancel()

/// The learner said they are done: what was heard so far is written down without waiting for the pause.
let mutable private earFinish = false

/// The learner can answer aloud: the setting is on, and there is a microphone and a key (for transcribing).
let private canHear (settings: Settings) =
    settings.AnswerAloud && Settings.hasKey settings && (platform ()).Recorder.IsSome

/// Listens: waits for speech, then for the pause that ends it, and sends what was said ("" if nothing was).
let private hearing (settings: Settings) (purpose: Hearing) (ct: CancellationToken) (dispatch: Msg -> unit) : Task =
    task {
        match (platform ()).Recorder with
        | None -> dispatch (StudyHeard(purpose, Error "Recording isn't available on this device."))
        | Some recorder ->
            // how long to wait for speech to start, the pause that ends it, and the longest it may take
            let wait, pause, longest =
                match purpose with
                | Hearing.Answer -> 7.0, 1.0, 12.0
                | Hearing.Explanation -> 15.0, 2.5, 150.0
                | Hearing.Question -> 8.0, 1.6, 45.0
            try
                do! recorder.Start()
                dispatch (StudyEarOpened purpose)
                let clock = Diagnostics.Stopwatch.StartNew()
                let mutable floor = 0.0
                let mutable spoke = false
                let mutable lastLoud = 0.0
                let mutable finished = false
                let mutable shown = 0.0
                while not finished do
                    do! Task.Delay(80, ct)
                    let t = clock.Elapsed.TotalSeconds
                    let level = recorder.Level
                    if earFinish then
                        spoke <- spoke || level < 0.0
                        finished <- true
                    elif level < 0.0 then
                        // loudness can't be measured: a fixed time
                        if t > (match purpose with Hearing.Answer -> 5.0 | Hearing.Question -> 8.0 | Hearing.Explanation -> 60.0) then
                            spoke <- true
                            finished <- true
                    else
                        // the first moment tells how loud the surroundings are
                        if t < 0.35 then floor <- max floor level
                        elif level > Math.Clamp(floor * 3.0, 0.06, 0.3) then
                            spoke <- true
                            lastLoud <- t
                        if spoke && t - lastLoud > pause then finished <- true
                        elif not spoke && t > wait then finished <- true
                        elif t > longest then finished <- true
                        if t - shown > 0.15 then
                            shown <- t
                            dispatch (StudyEarLevel level)
                if not spoke then
                    recorder.Cancel()
                    dispatch (StudyHeard(purpose, Ok ""))
                else
                    dispatch (StudyEarTranscribing purpose)
                    let! audio, name = recorder.Stop()
                    let! text = Mistral.transcribe settings.MistralApiKey audio name ct
                    dispatch (StudyHeard(purpose, Ok(text.Trim())))
            with
            | :? OperationCanceledException -> recorder.Cancel()
            | e ->
                recorder.Cancel()
                dispatch (StudyHeard(purpose, Error(errorText (inner e))))
    }

let private chimePath () = Path.Combine((platform ()).DataDir, "chime-v1.wav")

// ----- the session

/// A question with its options in a fresh order, so where the right one sits gives nothing away.
let private newQuiz (idea: string option) (concept: string option) (purpose: QuizPurpose) (q: Question) =
    let order = [ 0 .. q.Options.Length - 1 ] |> List.sortBy (fun _ -> Random.Shared.Next())
    { Idea = idea; Concept = concept; Purpose = purpose; Question = q; Order = order; Choice = None; Spoken = false; Before = None }

/// A question on an idea: the lesson's first one not asked yet, else the learner's idea's one asked longest ago.
let private questionFor (s: StudyState) (concepts: Map<string, Concept>) (idea: string option) (concept: string option) (exclude: Set<string>) =
    let skip = Set.union exclude s.Progress.Reported
    let c = concept |> Option.bind concepts.TryFind
    let asked (q: Question) = c |> Option.exists (fun c -> c.Questions |> List.exists (fun x -> x.Id = q.Id && x.LastAsked.IsSome))
    idea
    |> Option.bind s.Lessons.TryFind
    |> Option.bind (fun l -> l.Checks |> List.tryFind (fun q -> not (skip.Contains q.Id) && not (asked q)))
    |> Option.orElse (c |> Option.bind (Knowledge.pick true skip))

/// The plan's idea that is the learner's idea, if it was met in this paper.
let private ideaOf (s: StudyState) (concept: string) =
    s.Progress.Links |> Map.tryFindKey (fun _ c -> c = concept)

/// What the conversation with the tutor is about now.
let private chatKey (now: StudyNow) =
    match now with
    | StudyNow.Teach i -> i
    | StudyNow.Quiz q -> q.Idea |> Option.orElse q.Concept |> Option.defaultValue ""
    | _ -> ""

/// Moves the session on; the conversation with the tutor stays while it is about the same idea.
let private goTo (now: StudyNow) (s: StudyState) =
    let key = chatKey now
    let s = { s with Now = now; Error = None; Tries = 0; Heard = ""; Aside = None }
    if key = "" || key = s.ChatAbout then s
    else { s with ChatAbout = key; Chat = []; Followups = []; Pending = None; Input = "" }

/// The tutor says something, then `next`. What it was saying, and the microphone, stop.
let private say (settings: Settings) (r: ReaderState) (s: StudyState) (said: Study.Said list) (next: Then) : StudyState * Cmd<Msg> =
    voiceCancel.Cancel()
    voiceCancel <- CancellationTokenSource.CreateLinkedTokenSource s.Cancel.Token
    stopEar ()
    let id = Guid.NewGuid()
    let ready =
        said
        |> List.indexed
        |> List.choose (fun (i, x) -> let path = clipPath settings r.Paper.Id x.Say in if File.Exists path then Some(i, path) else None)
        |> Map.ofList
    let v = { Id = id; Said = said; At = 0; Offset = 0; Ready = ready; Playing = s.Running; Then = next }
    let s = { s with Voice = Some v; Ear = Ear.Off }
    if said.IsEmpty then s, Cmd.ofMsg (StudyClipEnded(id, 0))
    else
        let make = if ready.Count = said.Length then Cmd.none else makeClips settings r.Paper.Id (Some id) said voiceCancel.Token
        s, Cmd.batch [ make; (if s.Running then Cmd.batch [ playClip settings v; tutorPlayback r s true ] else stopAudio) ]

/// Says a question (after `intro`), then listens for the answer.
let private askQuiz (settings: Settings) (r: ReaderState) (s: StudyState) (intro: string) (q: Quiz) =
    let s = goTo (StudyNow.Quiz q) s
    say settings r s (Study.said intro @ Study.questionSaid q.Question q.Order) Then.Answer

/// Teaches an idea: says its lesson, or waits for it to be written.
let private teach (settings: Settings) (r: ReaderState) (s: StudyState) (idea: string) =
    let s = goTo (StudyNow.Teach idea) s
    match s.Plan |> Option.bind (fun p -> p.Idea idea), s.Lessons.TryFind idea with
    | Some i, Some lesson -> say settings r s (Study.lessonSaid i lesson) Then.Question
    | _ ->
        voiceCancel.Cancel()
        { s with Voice = None }, stopAudio

/// The paper read aloud from `first` to `last`; then the tutor.
let private listen (settings: Settings) (r: ReaderState) (s: StudyState) (first: int) (last: int) =
    voiceCancel.Cancel()
    stopEar ()
    // the paper goes on from where it was left (moved back 15 s, or listened further without the tutor), and the tutor
    // comes in at the end of that stretch; what was skipped is still covered after it
    let here =
        if r.Current >= first && r.Current <= last then Some last
        else
            s.Plan
            |> Option.bind (fun plan -> Study.stops r.Script plan |> List.tryFind (fun st -> r.Current >= st.First && r.Current <= st.Last))
            |> Option.map (fun st -> st.Last)
    let r, last =
        match here with
        | Some l -> { r with Finished = false }, l
        | None -> { r with Current = first; Offset = 0; Finished = false; Held = None }, last
    let s = goTo (StudyNow.Listening last) { s with Voice = None; Ear = Ear.Off }
    if s.Running then
        let r, cmd = play r settings
        r, s, cmd
    else r, s, Cmd.batch [ stopAudio; moveSynth r.Current ]

let private ideaName (s: StudyState) (concepts: Map<string, Concept>) (idea: string option) (concept: string option) =
    idea
    |> Option.bind (fun i -> s.Plan |> Option.bind (fun p -> p.Idea i))
    |> Option.map (fun i -> i.Name)
    |> Option.orElse (concept |> Option.bind concepts.TryFind |> Option.map (fun c -> c.Name))
    |> Option.defaultValue "an idea"

/// Goes to the next step: an idea learned minutes ago, the paper's next section, an idea to learn (or a quick check
/// of a known one), a recap, or the end; and starts writing the lessons coming up.
let rec private advance (settings: Settings) (r: ReaderState) (s: StudyState) (concepts: Map<string, Concept>) : ReaderState * StudyState * Cmd<Msg> =
    match s.Plan with
    | None -> r, s, Cmd.none
    | Some plan ->
        let step, progress = Study.next DateTime.UtcNow r.Script plan s.Progress concepts s.Last
        let s = { s with Progress = progress; Feedback = None; RecapInput = ""; Grading = false }
        let r, s, cmd =
            match step with
            | Study.Step.Listen (first, last) -> listen settings r s first last
            | Study.Step.Teach idea ->
                let s, cmd = teach settings r s idea
                r, s, cmd
            | Study.Step.Check (idea, c) ->
                match questionFor s concepts None (Some c) Set.empty with
                | Some q ->
                    let intro = sprintf "You learned %s before. A quick question to see if you still know it." (ideaName s concepts (Some idea) (Some c))
                    let s, cmd = askQuiz settings r s intro (newQuiz (Some idea) (Some c) QuizPurpose.QuickCheck q)
                    r, s, cmd
                | None ->
                    let s, cmd = teach settings r { s with Progress = { s.Progress with Teach = s.Progress.Teach.Add idea } } idea
                    r, s, cmd
            | Study.Step.Review c ->
                let idea = ideaOf s c
                match questionFor s concepts idea (Some c) Set.empty with
                | Some q ->
                    let intro = sprintf "Back to %s, from a few minutes ago." (ideaName s concepts idea (Some c))
                    let s, cmd = askQuiz settings r s intro (newQuiz idea (Some c) QuizPurpose.Again q)
                    r, s, cmd
                | None -> advance settings r { s with Last = Some c } concepts
            | Study.Step.Recap part ->
                let s = goTo (StudyNow.Recap part) s
                let text = sprintf "Time to put part %d together, in your own words. %s" (part + 1) plan.Parts.[part].Recap
                let text = if canHear settings then text + " Take your time; I'm listening." else text
                let s, cmd = say settings r s (Study.said text) Then.Explain
                r, s, cmd
            | Study.Step.Finished ->
                let s = goTo StudyNow.Finished s
                let s, cmd = say settings r s (Study.said "That's the whole paper. What you learned comes back for review just before you'd forget it.") Then.Wait
                r, s, cmd
        let s, write = prefetch settings r s concepts
        r, s, Cmd.batch [ cmd; write; saveStudy r.Paper.Id s.Progress ]

/// Asks an idea's question after its lesson (or before it, when the lesson is skipped).
and private checkIdea (settings: Settings) (r: ReaderState) (s: StudyState) (concepts: Map<string, Concept>) (idea: string) (purpose: QuizPurpose) =
    let concept = s.Progress.Links.TryFind idea |> Option.filter concepts.ContainsKey
    match questionFor s concepts (Some idea) concept Set.empty with
    | Some q ->
        let intro = if purpose = QuizPurpose.TestOut then "Let's see if you know it already." else ""
        let s, cmd = askQuiz settings r s intro (newQuiz (Some idea) concept purpose q)
        r, s, cmd
    | None when purpose = QuizPurpose.TestOut -> r, { s with Error = Some "The lesson is still being written; its question comes with it." }, Cmd.none
    | None ->
        // a lesson without a question to check it: on to the next idea
        advance settings r { s with Progress = { s.Progress with Done = s.Progress.Done.Add(idea, Study.Outcome.Learned) } } concepts

/// What comes after the tutor has said everything.
and private voiceDone (settings: Settings) (r: ReaderState) (s: StudyState) (concepts: Map<string, Concept>) (next: Then) =
    let s = { s with Voice = s.Voice |> Option.map (fun v -> { v with Playing = false; At = v.Said.Length }) }
    match next, s.Now with
    | Then.Question, StudyNow.Teach idea -> checkIdea settings r s concepts idea QuizPurpose.Check
    | Then.Answer, StudyNow.Quiz { Choice = None } when canHear settings -> r, s, Cmd.ofMsg (StudyListen Hearing.Answer)
    | Then.Explain, StudyNow.Recap _ when canHear settings -> r, s, Cmd.ofMsg (StudyListen Hearing.Explanation)
    | Then.Next, _ -> advance settings r s concepts
    | _ -> r, s, tutorPlayback r s false

/// Pauses the session: the tutor, the microphone and the paper.
let private hold (r: ReaderState) (s: StudyState) : ReaderState * StudyState * Cmd<Msg> =
    voiceCancel.Cancel()
    stopEar ()
    let r, cmd = if r.Playing then pause r else r, Cmd.none
    // where in the clip it stopped, so going on picks up there
    let held (v: Voice) =
        let offset = if v.Playing && v.At < v.Said.Length && v.Ready.ContainsKey v.At then (platform ()).Player.PositionMs else v.Offset
        { v with Playing = false; Offset = offset }
    let s = { s with Running = false; Ear = Ear.Off; Voice = s.Voice |> Option.map held }
    r, s, Cmd.batch [ cmd; stopAudio; tutorPlayback r s false ]

/// The tutor says `v` from where it is (its clip and the place in it), making again what was stopped half made.
let private sayOn (settings: Settings) (r: ReaderState) (s: StudyState) (v: Voice) : StudyState * Cmd<Msg> =
    let v = { v with Playing = true }
    voiceCancel.Cancel()
    voiceCancel <- CancellationTokenSource.CreateLinkedTokenSource s.Cancel.Token
    let make = if v.Ready.Count = v.Said.Length then Cmd.none else makeClips settings r.Paper.Id (Some v.Id) v.Said voiceCancel.Token
    let s = { s with Voice = Some v; Running = true }
    s, Cmd.batch [ make; playClip settings v; tutorPlayback r s true ]

/// Clip lengths (ms), read once.
let private clipLengths = Collections.Concurrent.ConcurrentDictionary<string, int>()

let private clipMs (path: string) = clipLengths.GetOrAdd(path, fun p -> try Wav.durationMs p with _ -> 0)

/// Moves what the tutor is saying back (negative) or ahead by `deltaMs`, like the paper's 15 s buttons: going on if
/// the session was going, staying paused if it was paused. Ahead past the end is what comes after it.
let private seekVoice (settings: Settings) (r: ReaderState) (s: StudyState) (concepts: Map<string, Concept>) (deltaMs: int) =
    match s.Voice with
    // all said already: nothing ahead
    | Some v when deltaMs > 0 && v.At >= v.Said.Length -> r, s, Cmd.none
    | Some v when not v.Said.IsEmpty ->
        let length (i: int) = v.Ready.TryFind i |> Option.map clipMs |> Option.filter (fun d -> d > 0)
        let last = v.Said.Length - 1
        // where it is now: all said is the end of the last clip
        let start, pos =
            if v.At > last then last, defaultArg (length last) 0
            elif v.Playing && v.Ready.ContainsKey v.At then v.At, (platform ()).Player.PositionMs
            else v.At, v.Offset
        let mutable at = start
        let mutable pos = pos + deltaMs
        if deltaMs < 0 then
            while pos < 0 && at > 0 && (length (at - 1)).IsSome do
                at <- at - 1
                pos <- pos + (length at).Value
            pos <- max 0 pos
        else
            while at <= last && (match length at with Some d -> pos >= d | None -> false) do
                pos <- pos - (length at).Value
                at <- at + 1
            // a clip not made yet: from its start
            if at <= last && (length at).IsNone then pos <- 0
        stopEar ()
        let s = { s with Ear = Ear.Off; Tries = 0 }
        match at > last, s.Running with
        | true, true -> voiceDone settings r { s with Voice = Some { v with At = at; Offset = 0; Playing = false } } concepts v.Then
        | true, false -> r, { s with Voice = Some { v with At = at; Offset = 0; Playing = false } }, stopAudio
        | false, true ->
            let s, cmd = sayOn settings r s { v with At = at; Offset = pos }
            r, s, cmd
        | false, false -> r, { s with Voice = Some { v with At = at; Offset = pos; Playing = false } }, stopAudio
    | _ -> r, s, Cmd.none

/// Goes on from where the session was paused.
let private resume (settings: Settings) (r: ReaderState) (s: StudyState) (concepts: Map<string, Concept>) : ReaderState * StudyState * Cmd<Msg> =
    let s = { s with Running = true; Error = None }
    match s.Now, s.Voice with
    | (StudyNow.Listening _ | StudyNow.Starting), _ ->
        let r, cmd = play { r with Zoom = None } settings
        r, s, cmd
    | _, Some v when v.At < v.Said.Length ->
        let s, cmd = sayOn settings r s v
        r, s, cmd
    | _, Some v -> voiceDone settings r s concepts v.Then
    | StudyNow.Quiz ({ Choice = None } as q), None ->
        let s, cmd = askQuiz settings r s "" q
        r, s, cmd
    | (StudyNow.Teach _ | StudyNow.Finished), None -> r, s, Cmd.none
    | _ -> advance settings r s concepts

/// What the tutor has in front of it now.
let private momentOf (s: StudyState) (concepts: Map<string, Concept>) : Study.Moment option =
    let ofIdea (idea: Study.Idea) answered : Study.Moment =
        { Name = idea.Name; Goal = idea.Goal; Background = idea.Background; Section = idea.Section; Lesson = s.Lessons.TryFind idea.Id; Answered = answered }
    match s.Now, s.Plan with
    | StudyNow.Teach id, Some plan -> plan.Idea id |> Option.map (fun i -> ofIdea i None)
    | StudyNow.Quiz q, Some plan ->
        // the options as they were shown and said (shuffled), so the tutor's letters are the learner's
        let answered =
            q.Choice
            |> Option.map (fun c ->
                let shownAt i = q.Order |> List.tryFindIndex ((=) i) |> Option.defaultValue i
                let shown (xs: string list) = if xs.Length = q.Order.Length then q.Order |> List.map (fun i -> xs.[i]) else xs
                { q.Question with Options = shown q.Question.Options; Why = shown q.Question.Why; Correct = shownAt q.Question.Correct },
                c |> Option.map shownAt)
        match q.Idea |> Option.bind plan.Idea with
        | Some i -> Some(ofIdea i answered)
        | None ->
            q.Concept
            |> Option.bind concepts.TryFind
            |> Option.map (fun c -> { Name = c.Name; Goal = c.Definition; Background = c.Background; Section = 0; Lesson = None; Answered = answered })
    | _ -> None

let private askTutor (settings: Settings) (r: ReaderState) (s: StudyState) (moment: Study.Moment) (question: string) (quick: bool) : StudyState * Cmd<Msg> =
    let askId = Guid.NewGuid()
    let history = s.Chat
    let ct = s.Cancel.Token
    let id = r.Paper.Id
    let script = r.Script
    { s with Pending = Some(askId, question, ""); Error = None; Followups = []; Input = (if quick then s.Input else "") },
    background (fun dispatch ->
        task {
            try
                let! k = knowledgeFor settings id script ignore
                let! reply = Study.askTutor settings script k moment history question quick (fun t -> dispatch (StudyAskText(askId, t))) ct
                dispatch (StudyAnswered(askId, Ok reply))
            with e -> dispatch (StudyAnswered(askId, Error(errorText (inner e))))
        })

let private gradeRecap (settings: Settings) (r: ReaderState) (s: StudyState) (plan: Study.Plan) (part: int) (answer: string) : Cmd<Msg> =
    let ct = s.Cancel.Token
    let id = r.Paper.Id
    let script = r.Script
    background (fun dispatch ->
        task {
            try
                let! k = knowledgeFor settings id script ignore
                let! f = Study.feedback settings script k plan part answer (RecapText >> dispatch) ct
                dispatch (RecapDone(Ok f))
            with e -> dispatch (RecapDone(Error(errorText (inner e))))
        })

/// How far each paper's study plan is (ideas done, ideas), for the Learn screen.
let private studied (papers: PaperInfo list) : Map<string, int * int> =
    let p = paths ()
    papers
    |> List.choose (fun paper ->
        try
            let ideas = Study.planSoFar (File.ReadAllText(p.StudyPlan paper.Id)) |> List.length
            let progress = Study.loadProgress p paper.Id |> Option.defaultValue (Study.Progress.empty DateTime.UtcNow)
            if ideas > 0 then Some(paper.Id, (min ideas progress.Done.Count, ideas)) else None
        with _ -> None)
    |> Map.ofList

// ---------------------------------------------------------------------------------------------
// Find papers
// ---------------------------------------------------------------------------------------------

let private withDiscover (model: Model) (f: DiscoverState -> DiscoverState * Cmd<Msg>) : Model * Cmd<Msg> =
    let d, cmd = f model.Discover
    { model with Discover = d }, cmd

/// Titles and sources of the library papers, for marking results already there.
let private knownPapers (papers: PaperInfo list) =
    let p = paths ()
    papers
    |> List.map (fun x ->
        x,
        (match Discover.loadSource p x.Id with
         | Some (Some s) -> s
         | _ -> { Doi = None; Arxiv = None; OpenAlex = None }: Discover.Source))

let private startSearch (settings: Settings) (d: DiscoverState) (query: string) (page: int) : DiscoverState * Cmd<Msg> =
    let id = Guid.NewGuid()
    let key = settings.OpenAlexKey
    let work () =
        task {
            match Discover.parseQuery query with
            | Discover.Query.Words words -> return! Discover.search key words page CancellationToken.None
            | q ->
                let! found = Discover.lookup key q CancellationToken.None
                return { Items = Option.toList found; Total = (if found.IsSome then 1 else 0) }: Discover.Results
        }
    let search =
        match d.Search with
        | Some s when page > 1 -> { s with Loading = true; Error = None; Id = id; Page = page }
        | _ -> { Query = query; Results = []; Total = 0; Page = 1; Loading = true; Error = None; Id = id }
    { d with Search = Some search; Failed = None; Expanded = None },
    runTask work (fun r -> SearchDone(id, Ok r)) (fun e -> SearchDone(id, Error(errorText e)))

let rec update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Dismiss -> { model with Notice = None }, Cmd.none

    // ----- library and import
    | OpenDocument ->
        { model with Notice = None },
        runTask pickDocument DocumentPicked (fun e -> DocumentPicked None)
    | DocumentPicked None -> model, Cmd.none
    | DocumentPicked (Some (path, name)) -> model, Cmd.ofMsg (ImportFile(path, name))
    | ImportFile (path, name) ->
        match model.Screen with
        | Screen.Importing _ -> { model with Notice = Some "Wait for the current paper to finish first." }, Cmd.none
        | _ ->
            let cts = new CancellationTokenSource()
            let importId = Guid.NewGuid()
            let leave =
                match model.Screen with
                | Screen.Reader r -> Cmd.batch [ snd (pause r); Cmd.ofEffect (fun _ -> stopSynth (); (platform ()).EndPlayback()) ]
                | _ -> Cmd.none
            let settings = model.Settings
            let work =
                Cmd.ofEffect (fun dispatch ->
                    Task.Run(fun () ->
                        task {
                            try
                                try
                                    let! result =
                                        Import.run (platform ()) settings path name
                                            (fun step progress -> dispatch (ImportProgress(importId, step, progress))) cts.Token
                                    dispatch (ImportFinished(importId, Ok result))
                                with e ->
                                    let e = match e with :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException | e -> e
                                    dispatch (ImportFinished(importId, Error(errorText e)))
                            finally
                                // picked files are temporary copies
                                if path.StartsWith(Path.Combine((platform ()).DataDir, "incoming")) then
                                    try File.Delete path with _ -> ()
                        }
                        :> Task)
                    |> ignore)
            { model with Screen = Screen.Importing { Id = importId; Name = name; Step = "Starting"; Progress = None; Cancel = cts }; Notice = None },
            Cmd.batch [ leave; work ]
    | ImportProgress (importId, step, progress) ->
        match model.Screen with
        | Screen.Importing s when s.Id = importId -> { model with Screen = Screen.Importing { s with Step = step; Progress = progress } }, Cmd.none
        | _ -> model, Cmd.none
    | CancelImport ->
        match model.Screen with
        | Screen.Importing s ->
            s.Cancel.Cancel()
            { model with Screen = Screen.Library }, Cmd.none
        | _ -> model, Cmd.none
    | ImportFinished (importId, result) ->
        match model.Screen, result with
        | Screen.Importing s, Ok (paper, _, warning) when s.Id = importId ->
            let papers = loadLibrary ()
            let model = { model with Papers = papers; Decks = loadDecks papers; Notice = warning }
            // a paper imported before may already have audio: load it like a library paper
            model, Cmd.ofMsg (OpenPaper paper)
        | Screen.Importing s, Error e when s.Id = importId -> { model with Screen = Screen.Library; Notice = Some e }, Cmd.none
        | _ -> model, Cmd.none // cancelled meanwhile
    | AskDelete id -> { model with ConfirmDelete = Some id }, Cmd.none
    | DeletePaper id ->
        (try Store.deletePaper (paths ()) id with _ -> ())
        { model with Papers = loadLibrary (); ConfirmDelete = None; Decks = model.Decks.Remove id }, Cmd.none

    // ----- reader
    | OpenPaper paper when Import.needsRefresh model.Settings (paths ()) paper.Id ->
        // images from an older version: redraw them first (with an OCR check when a key is set)
        let p = paths ()
        let settings = model.Settings
        let importId = Guid.NewGuid()
        let work =
            Cmd.ofEffect (fun dispatch ->
                Task.Run(fun () ->
                    task {
                        try
                            match Store.loadScript p paper.Id with
                            | None -> dispatch (ImportFinished(importId, Error "This paper's cache is from an older version. Remove it and add the document again."))
                            | Some script ->
                                let! script =
                                    Import.refreshCrops (platform ()) settings p paper.Id script (fun step progress ->
                                        dispatch (ImportProgress(importId, step, progress)))
                                dispatch (ImportFinished(importId, Ok(paper, script, None)))
                        with e ->
                            let e = match e with :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException | e -> e
                            dispatch (ImportFinished(importId, Error(errorText e)))
                    }
                    :> Task)
                |> ignore)
        { model with
            ConfirmDelete = None
            Screen = Screen.Importing { Id = importId; Name = paper.Title; Step = "Updating the equations and figures"; Progress = None; Cancel = new CancellationTokenSource() } },
        work
    | OpenPaper paper ->
        let model = { model with ConfirmDelete = None }
        let p = paths ()
        let key = Settings.voiceKey model.Settings
        let load () =
            task {
                match Store.loadScript p paper.Id with
                | None -> return failwith "This paper's cache is from an older version. Remove it and add the document again."
                | Some script ->
                    do! (platform ()).Restore(p.AudioDir(paper.Id, key))
                    let durations = Synth.cachedDurations (fun i -> p.Audio(paper.Id, key, i)) script.Segments.Length
                    return paper, script, durations
            }
        model, runTask load (Ok >> PaperLoaded) (errorText >> Error >> PaperLoaded)
    | PaperLoaded (Error e) -> { model with Notice = Some e; StudyOnOpen = None }, Cmd.none
    | PaperLoaded (Ok (paper, script, durations)) ->
        let r =
            { Paper = paper
              Script = script
              Current = min paper.LastSegment (max 0 (script.Segments.Length - 1))
              Offset = 0
              Playing = false
              Waiting = false
              Generation = 0
              Durations = durations
              VoiceKey = Settings.voiceKey model.Settings
              Error = None
              Finished = false
              ShowOutline = false
              ShowEquations = false
              Zoom = None
              Stops = Narration.equationStops script
              Held = None
              Help = None
              Cards = None
              Study = None }
        // papers open in Study (with a key), unless the learner turned it off
        let studying = Settings.hasKey model.Settings && (model.StudyOnOpen = Some paper.Id || model.Settings.Study)
        let r, playCmd = if studying then r, Cmd.none else play r model.Settings
        { model with Screen = Screen.Reader r; StudyOnOpen = None },
        Cmd.batch [ startSynth r model.Settings; playCmd; (if studying then Cmd.ofMsg OpenStudy else Cmd.none) ]
    | CloseReader ->
        match model.Screen with
        | Screen.Reader r ->
            r.Study |> Option.iter (fun s -> s.Cancel.Cancel())
            voiceCancel.Cancel()
            stopEar ()
            let _, cmd = pause r
            { model with Screen = Screen.Library; Papers = loadLibrary () |> List.map (fun p -> if p.Id = r.Paper.Id then { p with LastSegment = r.Current } else p) },
            Cmd.batch [ cmd; Cmd.ofEffect (fun _ -> stopSynth (); (platform ()).EndPlayback()) ]
        | _ -> model, Cmd.none
    | TogglePlay when (match model.Screen with Screen.Reader r -> r.Help.IsSome | _ -> false) -> update (CloseHelp true) model
    | TogglePlay when (match model.Screen with Screen.Reader r -> r.Cards.IsSome | _ -> false) -> update (CloseCards true) model
    | TogglePlay when
        (match model with
         | Studying (_, s) ->
             s.ShowTutor && s.Ear = Ear.Off
             && (s.Aside.IsNone || s.Voice |> Option.forall (fun v -> not v.Playing && v.At >= v.Said.Length))
         | _ -> false)
        ->
        // nothing answered yet, or the answer has been said: Continue goes back to the lesson
        update ToggleTutor model
    | TogglePlay when (match model with Studying _ -> true | _ -> false) ->
        match model with
        | Studying (r, s) ->
            match s.Now with
            | StudyNow.Listening _
            | StudyNow.Starting ->
                // the paper being read: its play and pause, which the session follows
                let r, cmd = if r.Playing then pause r else play { r with Zoom = None } model.Settings
                setStudy model r { s with Running = r.Playing }, cmd
            | _ ->
                let r, s, cmd = if s.Running && (s.Voice |> Option.exists (fun v -> v.Playing) || s.Ear <> Ear.Off) then hold r s else resume model.Settings r s model.Concepts
                setStudy model r s, cmd
        | _ -> model, Cmd.none
    | TogglePlay ->
        withReader model (fun r ->
            if r.Playing then pause r
            else
                // carrying on from the full-screen view goes back to the player
                let r = { r with Zoom = None }
                if r.Finished then play { r with Current = 0; Offset = 0 } model.Settings else play r model.Settings)
    | Remote wanted when (match model with Studying _ -> true | _ -> false) ->
        match model with
        | Studying (r, s) ->
            let busy = r.Playing || s.Voice |> Option.exists (fun v -> v.Playing) || s.Ear <> Ear.Off
            if wanted <> busy then update TogglePlay model else model, Cmd.none
        | _ -> model, Cmd.none
    | Remote wanted ->
        withReader model (fun r ->
            if wanted && not r.Playing then
                if r.Finished then play { r with Current = 0; Offset = 0 } model.Settings else play r model.Settings
            elif not wanted && r.Playing then pause r
            else r, Cmd.none)
    | Back15
    | Forward15 when (match model.Screen with Screen.Reader r -> r.Help.IsSome | _ -> false) ->
        // from Ask: back to the paper, moved and playing
        let model, close = update (CloseHelp false) model
        let model, move = update msg model
        let model, go = match model.Screen with Screen.Reader r when not r.Playing -> update TogglePlay model | _ -> model, Cmd.none
        model, Cmd.batch [ close; move; go ]
    | Back15
    | Forward15 when (match model with Studying (_, s) -> tutorTurn s | _ -> false) ->
        match model with
        | Studying (_, s) when s.ShowTutor && s.Aside.IsNone ->
            // nothing asked yet: the buttons are the lesson's, so back to it
            let model, close = update ToggleTutor model
            let model, move = update msg model
            model, Cmd.batch [ close; move ]
        | Studying (r, s) ->
            let amount = int (float jumpMs * model.Settings.Speed)
            let r, s, cmd = seekVoice model.Settings r s model.Concepts (match msg with Back15 -> -amount | _ -> amount)
            setStudy model r s, cmd
        | _ -> model, Cmd.none
    | Back15 ->
        withReader model (fun r ->
            let pos = currentPosition r
            let amount = int (float jumpMs * model.Settings.Speed)
            seek r model.Settings (Timeline.back (fun i -> r.Durations.TryFind i) amount pos))
    | Forward15 ->
        withReader model (fun r ->
            let pos = currentPosition r
            let amount = int (float jumpMs * model.Settings.Speed)
            seek r model.Settings (Timeline.forward (fun i -> r.Durations.TryFind i) r.Script.Segments.Length amount pos))
    | JumpToSegment i ->
        withReader model (fun r ->
            r.Help |> Option.iter (fun h -> h.Cancel.Cancel())
            // hearing an equation again from its full-screen view keeps it full screen
            let zoom = if r.Zoom.IsSome && i >= 0 && i < r.Script.Segments.Length && r.Zoom = r.Script.Segments.[i].Show then r.Zoom else None
            let r = { r with ShowOutline = false; ShowEquations = false; Zoom = zoom; Help = None; Cards = None }
            // studying: the tutor waits, and comes in at the end of the stretch now being heard
            let r, stopTutor =
                match r.Study with
                | Some s ->
                    let _, s, cmd = hold { r with Playing = false } s
                    let now =
                        match s.Plan with
                        | Some plan ->
                            Study.stops r.Script plan
                            |> List.tryFind (fun st -> i >= st.First && i <= st.Last)
                            |> Option.map (fun st -> StudyNow.Listening st.Last)
                            |> Option.defaultValue s.Now
                        | None -> StudyNow.Starting
                    { r with Study = Some(goTo now { s with Running = true; ShowPlan = false; ShowTutor = false; Voice = None }) }, cmd
                | None -> r, Cmd.none
            let r, cmd = seek r model.Settings { Segment = i; OffsetMs = 0 }
            let r, cmd = if r.Playing then r, cmd else play r model.Settings
            r, Cmd.batch [ stopTutor; cmd ])
    | CycleSpeed ->
        let i = speeds |> Array.tryFindIndex (fun s -> abs (s - model.Settings.Speed) < 0.01) |> Option.defaultValue 1
        let speed = speeds.[(i + 1) % speeds.Length]
        let settings = { model.Settings with Speed = speed }
        { model with Settings = settings },
        Cmd.batch [ saveSettings settings; Cmd.ofEffect (fun _ -> (platform ()).Player.SetSpeed speed) ]
    | Tick position ->
        withReader model (fun r -> (if r.Playing && not r.Waiting then { r with Offset = position } else r), Cmd.none)
    | ClipEnded gen when
        (match model with
         | Studying (r, { Now = StudyNow.Listening last }) -> gen = r.Generation && r.Playing && r.Current >= last
         | _ -> false)
        ->
        match model with
        | Studying (r, s) ->
            // the stretch has been heard: the tutor's turn
            let heard = r.Current + 1
            let r =
                { r with
                    Current = min heard (r.Script.Segments.Length - 1)
                    Offset = 0
                    Playing = false
                    Waiting = false
                    Held = None
                    Generation = r.Generation + 1 }
            let s = { s with Progress = { s.Progress with Heard = max s.Progress.Heard heard } }
            let r, s, cmd = advance model.Settings r s model.Concepts
            setStudy model r s, Cmd.batch [ playbackState r false; saveProgress r; moveSynth r.Current; cmd ]
        | _ -> model, Cmd.none
    | ClipEnded gen ->
        let model, keep =
            // studying: what has been heard so far, for going on from there
            match model with
            | Studying (r, s) when gen = r.Generation && r.Playing ->
                match s.Now with
                | StudyNow.Listening _
                | StudyNow.Starting when r.Current + 1 > s.Progress.Heard ->
                    let progress = { s.Progress with Heard = r.Current + 1 }
                    setStudy model r { s with Progress = progress }, saveStudy r.Paper.Id progress
                | _ -> model, Cmd.none
            | _ -> model, Cmd.none
        withReader model (fun r ->
            if gen <> r.Generation || not r.Playing then r, Cmd.none
            elif stopsHere model.Settings r && r.Current + 1 < r.Script.Segments.Length then
                // the equation has been read and explained: keep it up and wait for the listener
                let r =
                    { r with
                        Held = Some r.Stops.[r.Current]
                        Current = r.Current + 1
                        Offset = 0
                        Playing = false
                        Waiting = false
                        Generation = r.Generation + 1 }
                r, Cmd.batch [ stopAudio; playbackState r false; saveProgress r; moveSynth r.Current ]
            elif r.Current + 1 < r.Script.Segments.Length then
                let r = { r with Current = r.Current + 1; Offset = 0 }
                let r, cmd = play r model.Settings
                r, Cmd.batch [ cmd; saveProgress r ]
            else
                { r with Playing = false; Waiting = false; Finished = true; Offset = 0 },
                Cmd.batch [ playbackState r false; saveProgress { r with Current = 0 } ])
        |> fun (m, cmd) -> m, Cmd.batch [ cmd; keep ]
    | ClipReady (paperId, key, i, d) ->
        withReader model (fun r ->
            if r.Paper.Id <> paperId || r.VoiceKey <> key then r, Cmd.none
            else
                let r = { r with Durations = r.Durations.Add(i, d); Error = (if i = r.Current then None else r.Error) }
                if r.Waiting && r.Playing && i = r.Current then play r model.Settings else r, Cmd.none)
    | ClipFailed (paperId, i, message) ->
        withReader model (fun r ->
            if r.Paper.Id = paperId && (i = r.Current || i = r.Current + 1) then { r with Error = Some message }, Cmd.none
            else r, Cmd.none)
    | PlayerFailed message ->
        withReader model (fun r -> { r with Playing = false; Waiting = false; Error = Some("Playback failed: " + message) }, playbackState r false)
    | ToggleOutline -> withReader model (fun r -> { r with ShowOutline = not r.ShowOutline; ShowEquations = false; Zoom = None }, Cmd.none)
    | ToggleEquations -> withReader model (fun r -> { r with ShowEquations = not r.ShowEquations; ShowOutline = false; Zoom = None }, Cmd.none)
    | ToggleZoom ->
        withReader model (fun r ->
            match r.Zoom with
            | Some _ -> { r with Zoom = None }, Cmd.none
            | None -> { r with Zoom = (match r.Held with Some v -> Some v | None -> r.Script.Segments.[r.Current].Show) }, Cmd.none)
    | ZoomVisual v -> withReader model (fun r -> { r with Zoom = Some v }, Cmd.none)

    // ----- Ask
    | OpenHelp _ when
        (match model with
         | Studying (_, s) -> (match s.Now with StudyNow.Listening _ | StudyNow.Starting -> false | _ -> true)
         | _ -> false)
        ->
        update ToggleTutor model
    | OpenHelp about ->
        withReader model (fun r ->
            let wasPlaying = r.Playing
            let r, pauseCmd = if r.Playing then pause r else r, Cmd.none
            let seg = r.Script.Segments.[r.Current]
            // what "this" is: the equation kept on screen, the one shown with the sentence, or the one picked
            let about =
                match about with
                | Some v -> Some v
                | None ->
                    match r.Held with
                    | Some v -> Some v
                    | None -> seg.Show |> Option.filter (fun v -> r.Script.Visual v |> Option.exists (fun v -> v.Kind <> VisualKind.Inline))
            // stopped at an equation: Current already points at the next sentence, ask about the one just heard
            let position = if r.Held.IsSome && r.Current > 0 then r.Current - 1 else r.Current
            let history = try Store.loadHelp (paths ()) r.Paper.Id with _ -> []
            let known = match knowledge.TryGetValue r.Paper.Id with | true, k -> Some k | _ -> None
            let hasKey = Settings.hasKey model.Settings
            let h =
                { Position = position
                  About = about
                  History = history
                  Earlier = history.Length
                  ShowEarlier = false
                  Knowledge = known
                  Preparing = (if known.IsNone && hasKey then Some "Getting ready" else None)
                  Queued = None
                  Pending = None
                  Input = ""
                  Error = None
                  Mic = Mic.Idle
                  Speaking = None
                  SpeakQueue = []
                  SpeakPlaying = false
                  SpeakMade = false
                  ResumeOnClose = wasPlaying
                  Cancel = new CancellationTokenSource() }
            { r with Help = Some h; ShowEquations = false; ShowOutline = false; Zoom = None },
            Cmd.batch [ pauseCmd; (if known.IsNone && hasKey then prepareHelp model.Settings r else Cmd.none) ])
    | CloseHelp resume ->
        withReader model (fun r ->
            match r.Help with
            | None -> r, Cmd.none
            | Some h ->
                h.Cancel.Cancel()
                (platform ()).Recorder |> Option.iter (fun rec' -> if h.Mic = Mic.Recording then rec'.Cancel())
                let r = { r with Help = None; Zoom = None }
                let stop = stopAnswerAudio h
                if resume || h.ResumeOnClose then
                    let r, cmd = play r model.Settings
                    r, Cmd.batch [ stop; cmd ]
                else r, stop)
    | HelpStep (id, step) ->
        withHelp model (fun r h -> (if r.Paper.Id = id && h.Knowledge.IsNone then { h with Preparing = Some step } else h), Cmd.none)
    | HelpPrepared (id, result) ->
        withHelp model (fun r h ->
            if r.Paper.Id <> id then h, Cmd.none
            else
                match result with
                | Ok k ->
                    let h = { h with Knowledge = Some k; Preparing = None }
                    match h.Queued with
                    | Some (ask, byVoice) -> startAsk model.Settings r h k ask byVoice
                    | None -> h, Cmd.none
                | Error e -> { h with Preparing = None; Queued = None; Error = Some e }, Cmd.none)
    | AskHelp (ask, byVoice) ->
        withHelp model (fun r h ->
            if not (Settings.hasKey model.Settings) then { h with Error = Some "Add a Mistral API key in Settings to ask questions." }, Cmd.none
            elif h.Pending.IsSome then h, Cmd.none
            else
                let stop = stopAnswerAudio h
                let h = notSpeaking h
                match h.Knowledge with
                | Some k ->
                    let h, cmd = startAsk model.Settings r h k ask byVoice
                    h, Cmd.batch [ stop; cmd ]
                | None when h.Preparing.IsSome ->
                    // asked while the paper is being prepared: show the question now, ask when ready
                    { h with Queued = Some(ask, byVoice); Error = None
                             Pending = Some { Id = Guid.Empty; Question = Help.questionText ask (h.About |> Option.bind r.Script.Visual); Partial = ""; ByVoice = byVoice } },
                    stop
                | None ->
                    { h with Queued = Some(ask, byVoice); Preparing = Some "Getting ready"; Error = None
                             Pending = Some { Id = Guid.Empty; Question = Help.questionText ask (h.About |> Option.bind r.Script.Visual); Partial = ""; ByVoice = byVoice } },
                    Cmd.batch [ stop; prepareHelp model.Settings r ])
    | HelpText (askId, text) ->
        withHelp model (fun _ h ->
            match h.Pending with
            | Some p when p.Id = askId -> { h with Pending = Some { p with Partial = text } }, Cmd.none
            | _ -> h, Cmd.none)
    | HelpAnswered (askId, result) ->
        withHelp model (fun r h ->
            match h.Pending, result with
            | Some p, Ok turn when p.Id = askId ->
                let history = h.History @ [ turn ]
                let save = Cmd.ofEffect (fun _ -> try Store.saveHelp (paths ()) r.Paper.Id history with _ -> ())
                { h with History = history; Pending = None },
                Cmd.batch [ save; (if p.ByVoice then Cmd.ofMsg (SpeakAnswer turn) else Cmd.none) ]
            | Some p, Error e when p.Id = askId -> { h with Pending = None; Error = Some e }, Cmd.none
            | _ -> h, Cmd.none)
    | SetHelpInput text -> withHelp model (fun _ h -> { h with Input = text }, Cmd.none)
    | SendHelpInput ->
        match model.Screen with
        | Screen.Reader { Help = Some h } when h.Input.Trim() <> "" -> update (AskHelp(Help.Ask.Free h.Input, false)) model
        | _ -> model, Cmd.none
    | ToggleEarlier -> withHelp model (fun _ h -> { h with ShowEarlier = not h.ShowEarlier }, Cmd.none)
    | MicPressed ->
        withHelp model (fun _ h ->
            match (platform ()).Recorder, h.Mic with
            | None, _ -> { h with Error = Some "Recording isn't available on this device." }, Cmd.none
            | Some _, _ when not (Settings.hasKey model.Settings) -> { h with Error = Some "Add a Mistral API key in Settings to ask questions." }, Cmd.none
            | Some recorder, Mic.Idle ->
                { notSpeaking h with Error = None },
                Cmd.batch [ stopAnswerAudio h; runTask (fun () -> recorder.Start()) (Ok >> MicStarted) (errorText >> Error >> MicStarted) ]
            | Some recorder, Mic.Recording ->
                let key = model.Settings.MistralApiKey
                let ct = h.Cancel.Token
                let work () =
                    task {
                        let! audio, name = recorder.Stop()
                        return! Mistral.transcribe key audio name ct
                    }
                { h with Mic = Mic.Transcribing }, runTask work (Ok >> Transcribed) (errorText >> Error >> Transcribed)
            | Some _, Mic.Transcribing -> h, Cmd.none)
    | MicStarted result ->
        withHelp model (fun _ h ->
            match result with
            | Ok () -> { h with Mic = Mic.Recording }, Cmd.none
            | Error e -> { h with Mic = Mic.Idle; Error = Some("Couldn't use the microphone: " + e) }, Cmd.none)
    | Transcribed result ->
        match result with
        | Ok text when text.Trim().Length > 1 ->
            let typed = match model.Screen with Screen.Reader { Help = Some h } -> h.Input.Trim() | _ -> ""
            let model, _ = withHelp model (fun _ h -> { h with Mic = Mic.Idle }, Cmd.none)
            // said after typing part of it: the whole question
            update (AskHelp(Help.Ask.Free(if typed = "" then text else typed + " " + text), true)) model
        | Ok _ -> withHelp model (fun _ h -> { h with Mic = Mic.Idle; Error = Some "I didn't catch that. Try again, a little closer to the microphone." }, Cmd.none)
        | Error e -> withHelp model (fun _ h -> { h with Mic = Mic.Idle; Error = Some e }, Cmd.none)
    | SpeakAnswer turn ->
        withHelp model (fun r h ->
            match engineFor model.Settings with
            | None -> { h with Error = Some "No voice is available to read the answer." }, Cmd.none
            | Some engine ->
                let stop = stopAnswerAudio h
                speakCancel <- CancellationTokenSource.CreateLinkedTokenSource h.Cancel.Token
                let ct = speakCancel.Token
                let p = paths ()
                let asked = turn.AskedUtc
                let chunks = Help.speechChunks turn.Answer
                // clips are made in order and played as soon as each is ready
                let work =
                    Cmd.ofEffect (fun dispatch ->
                        Task.Run(fun () ->
                            task {
                                try
                                    Directory.CreateDirectory(p.HelpDir r.Paper.Id) |> ignore
                                    for i, text in List.indexed chunks do
                                        let! wav = engine.Synthesize(text, ct)
                                        ct.ThrowIfCancellationRequested()
                                        let path = p.AnswerAudio(r.Paper.Id, i)
                                        File.WriteAllBytes(path, wav)
                                        dispatch (AnswerAudio(asked, Ok(path, (i = chunks.Length - 1))))
                                with
                                | :? OperationCanceledException -> ()
                                | e -> dispatch (AnswerAudio(asked, Error(errorText e)))
                            }
                            :> Task)
                        |> ignore)
                { notSpeaking h with Speaking = Some asked }, Cmd.batch [ stop; work ])
    | AnswerAudio (asked, result) ->
        withHelp model (fun _ h ->
            if h.Speaking <> Some asked then h, Cmd.none
            else
                match result with
                | Ok (path, last) ->
                    let h = { h with SpeakMade = last }
                    if h.SpeakPlaying then { h with SpeakQueue = h.SpeakQueue @ [ path ] }, Cmd.none
                    else { h with SpeakPlaying = true }, playAnswerClip model.Settings.Speed asked path
                | Error e -> { notSpeaking h with Error = Some e }, Cmd.none)
    | AnswerSpoken asked ->
        withHelp model (fun _ h ->
            if h.Speaking <> Some asked then h, Cmd.none
            else
                match h.SpeakQueue with
                | next :: rest -> { h with SpeakQueue = rest }, playAnswerClip model.Settings.Speed asked next
                | [] when h.SpeakMade -> notSpeaking h, Cmd.none
                | [] -> { h with SpeakPlaying = false }, Cmd.none)
    | StopSpeaking -> withHelp model (fun _ h -> notSpeaking h, stopAnswerAudio h)
    | SetAboutMe text ->
        let settings = { model.Settings with AboutMe = text }
        { model with Settings = settings }, saveSettings settings
    | SetHelpModel m ->
        let settings = { model.Settings with HelpModel = (if String.IsNullOrWhiteSpace m then Settings.defaults.HelpModel else m.Trim()) }
        { model with Settings = settings }, saveSettings settings

    // ----- Find papers
    | OpenDiscover ->
        let model = { model with Screen = Screen.Discover; Notice = None; ConfirmDelete = None }
        let model = { model with Discover = { model.Discover with Known = knownPapers model.Papers } }
        let seeds = model.Papers |> List.map (fun p -> p.Id)
        match model.Discover.Recs with
        | Recs.Ready (_, s) when s = seeds -> model, Cmd.none
        | Recs.Loading _ -> model, Cmd.none
        | _ -> update (LoadRecs false) model
    | CloseDiscover ->
        withDiscover { model with Screen = Screen.Library } (fun d ->
            d.Fetching |> Option.iter (fun (_, _, cts) -> cts.Cancel())
            { d with Fetching = None }, Cmd.none)
    | SetDiscoverInput text ->
        withDiscover model (fun d ->
            // clearing the box goes back to the recommendations
            let d = { d with Input = text }
            if String.IsNullOrWhiteSpace text then { d with Search = None; Failed = None }, Cmd.none else d, Cmd.none)
    | RunSearch ->
        let q = model.Discover.Input.Trim()
        if q.Length < 2 then model, Cmd.none
        else withDiscover model (fun d -> startSearch model.Settings d q 1)
    | SearchTopic topic -> withDiscover model (fun d -> startSearch model.Settings { d with Input = topic } topic 1)
    | SearchMore ->
        withDiscover model (fun d ->
            match d.Search with
            | Some s when not s.Loading && s.Results.Length < s.Total -> startSearch model.Settings d s.Query (s.Page + 1)
            | _ -> d, Cmd.none)
    | SearchDone (id, result) ->
        withDiscover model (fun d ->
            match d.Search with
            | Some s when s.Id = id ->
                let s =
                    match result with
                    | Ok r ->
                        let results = s.Results @ r.Items |> List.distinctBy (fun f -> f.Key)
                        // a page can come back short (results without a usable PDF are left out)
                        { s with Results = results; Total = (if r.Items.IsEmpty then results.Length else r.Total); Loading = false }
                    | Error e -> { s with Loading = false; Error = Some e }
                { d with Search = Some s }, Cmd.none
            | _ -> d, Cmd.none)
    | ClearSearch -> withDiscover model (fun d -> { d with Input = ""; Search = None; Failed = None; Expanded = None }, Cmd.none)
    | LoadRecs force ->
        let papers = model.Papers
        let seeds = papers |> List.map (fun p -> p.Id)
        if papers.IsEmpty then withDiscover model (fun d -> { d with Recs = Recs.Ready([], seeds) }, Cmd.none)
        else
            match model.Discover.Recs with
            | Recs.Loading _ when not force -> model, Cmd.none
            | _ ->
                let key = model.Settings.OpenAlexKey
                let p = paths ()
                let work =
                    Cmd.ofEffect (fun dispatch ->
                        Task.Run(fun () ->
                            task {
                                try
                                    let! recs = Discover.forLibrary key p papers (RecsStep >> dispatch) CancellationToken.None
                                    dispatch (RecsDone(seeds, Ok recs))
                                with e ->
                                    let e = match e with :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException | e -> e
                                    dispatch (RecsDone(seeds, Error(errorText e)))
                            }
                            :> Task)
                        |> ignore)
                withDiscover model (fun d -> { d with Recs = Recs.Loading "Getting recommendations" }, work)
    | RecsStep step ->
        withDiscover model (fun d -> (match d.Recs with Recs.Loading _ -> { d with Recs = Recs.Loading step } | _ -> d), Cmd.none)
    | RecsDone (seeds, result) ->
        withDiscover model (fun d ->
            let d = { d with Known = knownPapers model.Papers }
            match result with
            | Ok recs -> { d with Recs = Recs.Ready(recs, seeds) }, Cmd.none
            | Error e -> { d with Recs = Recs.Failed e }, Cmd.none)
    | ToggleAbstract key -> withDiscover model (fun d -> { d with Expanded = (if d.Expanded = Some key then None else Some key) }, Cmd.none)
    | FetchPaper f ->
        match model.Screen with
        | Screen.Importing _ -> { model with Notice = Some "Wait for the current paper to finish first." }, Cmd.none
        | _ when model.Discover.Fetching.IsSome -> model, Cmd.none
        | _ ->
            let cts = new CancellationTokenSource()
            let folder = Path.Combine((platform ()).DataDir, "incoming")
            let p = paths ()
            let work =
                Cmd.ofEffect (fun dispatch ->
                    Task.Run(fun () ->
                        task {
                            try
                                let! path, name = Discover.download f folder (fun step -> dispatch (FetchStep(f.Key, step))) cts.Token
                                Discover.rememberSource p path f
                                dispatch (FetchDone(f.Key, Ok(path, name)))
                            with e ->
                                let e = match e with :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException | e -> e
                                dispatch (FetchDone(f.Key, Error(errorText e)))
                        }
                        :> Task)
                    |> ignore)
            withDiscover model (fun d -> { d with Fetching = Some(f.Key, "Downloading", cts); Failed = None }, work)
    | FetchStep (key, step) ->
        withDiscover model (fun d ->
            match d.Fetching with
            | Some (k, _, cts) when k = key -> { d with Fetching = Some(k, step, cts) }, Cmd.none
            | _ -> d, Cmd.none)
    | FetchDone (key, result) ->
        match model.Discover.Fetching, result with
        | Some (k, _, _), Ok (path, name) when k = key ->
            let model = { model with Discover = { model.Discover with Fetching = None } }
            match model.Screen with
            | Screen.Discover -> update (ImportFile(path, name)) model
            | _ -> model, Cmd.none
        | Some (k, _, _), Error e when k = key ->
            withDiscover model (fun d -> { d with Fetching = None; Failed = (if e = "Cancelled." then None else Some(key, e)) }, Cmd.none)
        | _ -> model, Cmd.none
    | CancelFetch ->
        withDiscover model (fun d ->
            d.Fetching |> Option.iter (fun (_, _, cts) -> cts.Cancel())
            { d with Fetching = None }, Cmd.none)
    | OpenLink url ->
        model,
        Cmd.ofEffect (fun _ ->
            match Services.topLevel, Uri.TryCreate(url, UriKind.Absolute) with
            | Some top, (true, uri) -> top.Launcher.LaunchUriAsync uri |> ignore
            | _ -> ())
    | SetOpenAlexKey key ->
        let settings = { model.Settings with OpenAlexKey = key.Trim() }
        { model with Settings = settings }, saveSettings settings

    // ----- Learn
    | OpenLearn ->
        { model with Screen = Screen.Learn; Notice = None; ConfirmDelete = None; Decks = loadDecks model.Papers; Studied = studied model.Papers }, Cmd.none
    | CloseLearn -> { model with Screen = Screen.Library; MakeError = None }, Cmd.none
    | ToggleDeck id -> { model with LearnOpen = (if model.LearnOpen = Some id then None else Some id) }, Cmd.none
    | OpenCards about when (match model with Studying (_, s) -> s.Running | _ -> false) ->
        // the tutor waits while cards are made
        match model with
        | Studying (r, s) ->
            let r, s, cmd = hold r s
            let m, open' = update (OpenCards about) (setStudy model r s)
            m, Cmd.batch [ cmd; open' ]
        | _ -> model, Cmd.none
    | OpenCards about ->
        withReader model (fun r ->
            let wasPlaying = r.Playing
            let r, pauseCmd = if r.Playing then pause r else r, Cmd.none
            let about =
                match about with
                | Some v -> Some v
                | None ->
                    match r.Held with
                    | Some v -> Some v
                    | None ->
                        r.Script.Segments.[r.Current].Show
                        |> Option.filter (fun v -> r.Script.Visual v |> Option.exists (fun v -> v.Kind <> VisualKind.Inline))
            let panel = { Position = cardPosition r; About = about; Input = ""; ResumeOnClose = wasPlaying }
            { r with Cards = Some panel; ShowEquations = false; ShowOutline = false; Zoom = None }, pauseCmd)
        |> fun (m, cmd) ->
            let studiedHere = match m.Screen with Screen.Reader r -> studied [ r.Paper ] | _ -> Map.empty
            { m with MakeError = None; Studied = Map.fold (fun acc k v -> Map.add k v acc) m.Studied studiedHere }, cmd
    | CloseCards resume ->
        withReader model (fun r ->
            match r.Cards with
            | None -> r, Cmd.none
            | Some c ->
                let r = { r with Cards = None }
                if resume || c.ResumeOnClose then play r model.Settings else r, Cmd.none)
    | SetCardInput text ->
        withReader model (fun r -> { r with Cards = r.Cards |> Option.map (fun c -> { c with Input = text }) }, Cmd.none)
    | SendCardInput ->
        match model.Screen with
        | Screen.Reader ({ Cards = Some c } as r) when c.Input.Trim() <> "" ->
            let model = { model with Screen = Screen.Reader { r with Cards = Some { c with Input = "" } } }
            update (MakeCards(r.Paper.Id, Cards.Request.Topic c.Input)) model
        | _ -> model, Cmd.none
    | MakeCards (paperId, request) ->
        if not (Settings.hasKey model.Settings) then { model with MakeError = Some "Add a Mistral API key in Settings to make cards." }, Cmd.none
        elif model.Making.IsSome then { model with MakeError = Some "Wait for the cards being made to finish." }, Cmd.none
        else
            let script, position =
                match model.Screen with
                | Screen.Reader r when r.Paper.Id = paperId -> Some r.Script, cardPosition r
                | _ -> None, 0
            let making, work = startMaking model.Settings paperId script position (deckOf model paperId) request
            { model with Making = Some making; Made = None; MakeError = None }, work
    | CardsStep (id, step) ->
        match model.Making with
        | Some m when m.Id = id -> { model with Making = Some { m with Step = Some step } }, Cmd.none
        | _ -> model, Cmd.none
    | CardsProgress (id, count) ->
        match model.Making with
        | Some m when m.Id = id -> { model with Making = Some { m with Step = None; Count = count } }, Cmd.none
        | _ -> model, Cmd.none
    | CardsMade (id, result) ->
        match model.Making, result with
        | Some m, Ok cards when m.Id = id ->
            let model, save = setDeck model m.PaperId (deckOf model m.PaperId @ cards)
            { model with Making = None; Made = Some(m.PaperId, cards) }, save
        | Some m, Error e when m.Id = id ->
            { model with Making = None; MakeError = (if e = "Cancelled." then None else Some e) }, Cmd.none
        | _ -> model, Cmd.none
    | CancelMaking ->
        model.Making |> Option.iter (fun m -> m.Cancel.Cancel())
        { model with Making = None }, Cmd.none
    | DeleteCard (paperId, cardId) ->
        let model, save = setDeck model paperId (deckOf model paperId |> List.filter (fun c -> c.Id <> cardId))
        let made = model.Made |> Option.map (fun (p, cards) -> p, cards |> List.filter (fun c -> c.Id <> cardId))
        let isCard =
            function
            | Knowledge.ReviewItem.Card (_, c) -> c.Id = cardId
            | _ -> false
        let review =
            model.Review
            |> Option.map (fun rv ->
                match rv.Queue with
                | item :: rest when isCard item -> reviewAt rv rest
                | q -> { rv with Queue = q |> List.filter (isCard >> not) })
        { model with Made = made; Review = review }, save
    | StartReview scope ->
        match reviewQueue model scope DateTime.UtcNow with
        | [] -> { model with Notice = Some "Nothing to review right now." }, Cmd.none
        | queue ->
            let model, pauseCmd = withReader model (fun r -> if r.Playing then pause r else r, Cmd.none)
            { model with Review = Some { Scope = scope; Queue = queue; Revealed = false; Choice = None; Order = shuffled queue; Answered = 0; Remembered = 0; Total = queue.Length } },
            pauseCmd
    | ShowAnswer -> withReview model (fun rv -> { rv with Revealed = true }, Cmd.none)
    | RateCard rating ->
        let now = DateTime.UtcNow
        let learning (m: Memory) = m.Stage = CardStage.Learning || m.Stage = CardStage.Relearning
        let counted (rv: ReviewState) = { rv with Answered = rv.Answered + 1; Remembered = rv.Remembered + (if rating = Fsrs.Rating.Again then 0 else 1) }
        match model.Review with
        | Some ({ Revealed = true; Queue = Knowledge.ReviewItem.Card (paperId, c) :: rest } as rv) ->
            let card = { c with Memory = Fsrs.review model.Settings.Retention now c.Id c.Memory rating }
            let model, save = setDeck model paperId (deckOf model paperId |> List.map (fun x -> if x.Id = c.Id then card else x))
            // a card still in its short steps comes back later in the session
            let rest = if learning card.Memory then rest @ [ Knowledge.ReviewItem.Card(paperId, card) ] else rest
            { model with Review = Some(reviewAt (counted rv) rest) }, save
        | Some ({ Revealed = true; Queue = Knowledge.ReviewItem.Concept (c, q) :: rest } as rv) when q.Options.IsEmpty ->
            // a recall question, rated like a card
            let c = model.Concepts.TryFind c.Id |> Option.defaultValue c |> Knowledge.answer model.Settings.Retention now rating q.Id
            let concepts = model.Concepts.Add(c.Id, c)
            let rest =
                match (if learning c.Memory then Knowledge.pick false (Set.singleton q.Id) c else None) with
                | Some next -> rest @ [ Knowledge.ReviewItem.Concept(c, next) ]
                | None -> rest
            { model with Concepts = concepts; Review = Some(reviewAt (counted rv) rest) }, saveConcepts concepts
        | _ -> model, Cmd.none
    | ReviewChoose choice ->
        match model.Review with
        | Some ({ Choice = None; Queue = Knowledge.ReviewItem.Concept (c, q) :: _ } as rv) when not q.Options.IsEmpty ->
            let rating = Knowledge.rating q choice
            let c = model.Concepts.TryFind c.Id |> Option.defaultValue c |> Knowledge.answer model.Settings.Retention DateTime.UtcNow rating q.Id
            let concepts = model.Concepts.Add(c.Id, c)
            let rv =
                { rv with
                    Choice = Some choice
                    Revealed = true
                    Answered = rv.Answered + 1
                    Remembered = rv.Remembered + (if rating = Fsrs.Rating.Again then 0 else 1) }
            { model with Concepts = concepts; Review = Some rv }, saveConcepts concepts
        | _ -> model, Cmd.none
    | ReviewKey n ->
        match model.Review with
        | Some ({ Choice = None; Queue = Knowledge.ReviewItem.Concept (_, q) :: _ } as rv) when not q.Options.IsEmpty ->
            // 1 to 4 pick the options in the order shown
            if n >= 1 && n <= rv.Order.Length then update (ReviewChoose(Some rv.Order.[n - 1])) model else model, Cmd.none
        | Some { Revealed = true; Choice = None } when n >= 1 && n <= 4 -> update (RateCard Fsrs.ratings.[n - 1]) model
        | _ -> model, Cmd.none
    | ReviewNext ->
        match model.Review with
        | Some ({ Choice = Some _; Queue = Knowledge.ReviewItem.Concept (_, q) :: rest } as rv) ->
            // an idea still in its short steps comes back later in the session, with another question
            let c = model.Concepts.TryFind (match rv.Queue.Head with Knowledge.ReviewItem.Concept (c, _) -> c.Id | _ -> "")
            let rest =
                match c with
                | Some c when c.Memory.Stage = CardStage.Learning || c.Memory.Stage = CardStage.Relearning ->
                    match Knowledge.pick false (Set.singleton q.Id) c with
                    | Some next -> rest @ [ Knowledge.ReviewItem.Concept(c, next) ]
                    | None -> rest
                | _ -> rest
            { model with Review = Some(reviewAt rv rest) }, Cmd.none
        | Some { Choice = None; Queue = Knowledge.ReviewItem.Concept (_, q) :: _ } when not q.Options.IsEmpty -> model, Cmd.none
        | Some { Revealed = false; Queue = _ :: _ } -> update ShowAnswer model
        | Some { Revealed = true } -> update (RateCard Fsrs.Rating.Good) model
        | Some { Queue = [] } -> update CloseReview model
        | None -> model, Cmd.none
    | CloseReview -> { model with Review = None }, Cmd.none
    | ListenToCard (paperId, segment) ->
        let model = { model with Review = None }
        match model.Screen with
        | Screen.Reader r when r.Paper.Id = paperId -> update (JumpToSegment segment) model
        | _ ->
            match model.Papers |> List.tryFind (fun p -> p.Id = paperId) with
            | Some paper ->
                let model, close = update CloseReader model
                let model, openCmd = update (OpenPaper { paper with LastSegment = segment }) model
                model, Cmd.batch [ close; openCmd ]
            | None -> model, Cmd.none
    | SetRetention r ->
        let settings = { model.Settings with Retention = r }
        { model with Settings = settings }, saveSettings settings
    | ToggleKnown -> { model with ShowKnown = not model.ShowKnown }, Cmd.none
    | ForgetConcept id ->
        let concepts = model.Concepts.Remove id
        { model with Concepts = concepts }, saveConcepts concepts

    // ----- Study
    | OpenStudy ->
        match model.Screen with
        | Screen.Reader r when r.Study.IsSome -> model, Cmd.none
        | Screen.Reader _ when not (Settings.hasKey model.Settings) ->
            { model with Notice = Some "Studying needs a Mistral API key (Settings): the tutor is a model that reads the whole paper." }, Cmd.none
        | Screen.Reader r ->
            let r, pauseCmd = if r.Playing then pause r else r, Cmd.none
            let r = { r with Cards = None; Help = None; ShowOutline = false; ShowEquations = false; Zoom = None }
            let p = paths ()
            let id = r.Paper.Id
            let plan = Study.loadPlan p id r.Script
            let progress = Study.loadProgress p id |> Option.defaultValue (Study.Progress.empty DateTime.UtcNow)
            // the plan's confirmed known ideas, for those not linked yet (and still known)
            let progress =
                match plan with
                | Some plan ->
                    let links =
                        plan.Ideas
                        |> List.fold
                            (fun (links: Map<string, string>) i ->
                                match i.Same with
                                | Some c when not (links.ContainsKey i.Id) && model.Concepts.ContainsKey c -> links.Add(i.Id, c)
                                | _ -> links)
                            progress.Links
                    { progress with Links = links }
                | None -> progress
            let lessons =
                match plan with
                | Some plan -> plan.Ideas |> List.choose (fun i -> Study.loadLesson p id r.Script i.Id |> Option.map (fun l -> i.Id, l)) |> Map.ofList
                | None -> Map.empty
            let s =
                { Plan = plan
                  Planning = None
                  Progress = progress
                  Lessons = lessons
                  Writing = Map.empty
                  Failed = Map.empty
                  Now = StudyNow.Starting
                  Running = true
                  Voice = None
                  Aside = None
                  Ear = Ear.Off
                  Tries = 0
                  Heard = ""
                  ChatAbout = ""
                  Chat = []
                  Followups = []
                  Pending = None
                  Input = ""
                  ShowTutor = false
                  ShowPlan = false
                  RecapInput = ""
                  Feedback = None
                  Grading = false
                  Error = None
                  Last = None
                  Cancel = new CancellationTokenSource() }
            match plan with
            | Some plan ->
                let r, s, cmd = advance model.Settings r s model.Concepts
                setStudy { model with MakeError = None } r s, Cmd.batch [ pauseCmd; cmd; rematch model.Settings r plan progress model.Concepts ]
            | None ->
                // the paper is read aloud from the start while the tutor reads it and plans
                let concepts = model.Concepts |> Map.toList |> List.map snd
                let r = { r with Current = min progress.Heard (r.Script.Segments.Length - 1); Offset = 0; Finished = false; Held = None }
                let r, playCmd = play r model.Settings
                let s = { s with Planning = Some("Getting ready", []) }
                setStudy { model with MakeError = None } r s, Cmd.batch [ pauseCmd; playCmd; writePlan model.Settings r concepts s.Cancel.Token ]
        | _ -> model, Cmd.none
    | StudyPaper paper ->
        match model.Screen with
        | Screen.Reader r when r.Paper.Id = paper.Id -> update OpenStudy model
        | _ ->
            let model, close = match model.Screen with Screen.Reader _ -> update CloseReader model | _ -> model, Cmd.none
            let model, openCmd = update (OpenPaper paper) { model with StudyOnOpen = Some paper.Id }
            model, Cmd.batch [ close; openCmd ]
    | CloseStudy ->
        match model with
        | Studying (r, s) ->
            let listening = match s.Now with StudyNow.Listening _ | StudyNow.Starting -> r.Playing | _ -> false
            s.Cancel.Cancel()
            let r, _, cmd = hold r s
            // the paper goes on being read aloud if it was
            let r, playCmd = if listening then play r model.Settings else r, Cmd.none
            let m = { model with Screen = Screen.Reader { r with Study = None } }
            { m with Studied = Map.fold (fun acc k v -> Map.add k v acc) m.Studied (studied [ r.Paper ]) }, Cmd.batch [ cmd; playCmd ]
        | _ -> model, Cmd.none
    | StudyStep (id, step) ->
        match model with
        | Studying (r, s) when r.Paper.Id = id && s.Planning.IsSome -> setStudy model r { s with Planning = Some(step, snd s.Planning.Value) }, Cmd.none
        | _ -> model, Cmd.none
    | StudyPlanText (id, text) ->
        match model with
        | Studying (r, s) when r.Paper.Id = id && s.Planning.IsSome ->
            let step = if text.Contains "IDEA" then "Planning the ideas to learn" else fst s.Planning.Value
            setStudy model r { s with Planning = Some(step, Study.planSoFar text) }, Cmd.none
        | _ -> model, Cmd.none
    | StudyPlanned (id, result) ->
        match model with
        | Studying (r, s) when r.Paper.Id = id ->
            match result with
            | Ok text ->
                let plan = Study.parsePlan r.Script text
                // ideas confirmed to be ones the learner knows
                let links =
                    plan.Ideas
                    |> List.choose (fun i -> i.Same |> Option.filter model.Concepts.ContainsKey |> Option.map (fun c -> i.Id, c))
                    |> Map.ofList
                let progress = { Study.Progress.empty DateTime.UtcNow with Links = links; Heard = s.Progress.Heard }
                let s = { s with Plan = Some plan; Planning = None; Progress = progress; Lessons = Map.empty }
                match s.Now with
                | StudyNow.Starting when r.Playing || not s.Running ->
                    // the tutor comes in at the end of the stretch being read
                    let last =
                        Study.stops r.Script plan
                        |> List.tryFind (fun st -> r.Current >= st.First && r.Current <= st.Last)
                        |> Option.map (fun st -> st.Last)
                        |> Option.defaultValue (r.Script.Segments.Length - 1)
                    let s, write = prefetch model.Settings r (goTo (StudyNow.Listening last) s) model.Concepts
                    setStudy model r s, Cmd.batch [ saveStudy id progress; write ]
                | StudyNow.Starting ->
                    // the paper was read to the end meanwhile
                    let r, s, cmd = advance model.Settings r s model.Concepts
                    setStudy model r s, cmd
                | _ ->
                    let s, write = prefetch model.Settings r s model.Concepts
                    setStudy model r s, Cmd.batch [ saveStudy id progress; write ]
            | Error e -> setStudy model r { s with Planning = None; Error = (if e = "Cancelled." then None else Some e) }, Cmd.none
        | _ -> model, Cmd.none
    | RetryPlan ->
        match model with
        | Studying (r, ({ Plan = None; Planning = None } as s)) ->
            let concepts = model.Concepts |> Map.toList |> List.map snd
            setStudy model r { s with Planning = Some("Getting ready", []); Error = None }, writePlan model.Settings r concepts s.Cancel.Token
        | _ -> model, Cmd.none
    | StudyLessonText (id, idea, text) ->
        match model with
        | Studying (r, s) when r.Paper.Id = id && s.Writing.ContainsKey idea -> setStudy model r { s with Writing = s.Writing.Add(idea, text) }, Cmd.none
        | _ -> model, Cmd.none
    | StudyLessonDone (id, idea, result) ->
        match model with
        | Studying (r, s) when r.Paper.Id = id ->
            let s = { s with Writing = s.Writing.Remove idea }
            match result with
            | Ok text ->
                let lesson = Study.parseLesson r.Script id idea text
                let s = { s with Lessons = s.Lessons.Add(idea, lesson) }
                let s, write = prefetch model.Settings r s model.Concepts
                match s.Now, s.Voice, s.Plan |> Option.bind (fun p -> p.Idea idea) with
                | StudyNow.Teach i, None, Some it when i = idea ->
                    // the lesson the learner is waiting for
                    let s, cmd = say model.Settings r s (Study.lessonSaid it lesson) Then.Question
                    setStudy model r s, Cmd.batch [ write; cmd ]
                | _, _, Some it ->
                    // its first words are made ahead, so the tutor starts at once
                    setStudy model r s, Cmd.batch [ write; makeClips model.Settings id None (Study.lessonSaid it lesson |> List.truncate 2) s.Cancel.Token ]
                | _ -> setStudy model r s, write
            | Error e -> setStudy model r { s with Failed = s.Failed.Add(idea, e) }, Cmd.none
        | _ -> model, Cmd.none
    | StudyRetryLesson idea ->
        match model with
        | Studying (r, s) ->
            let s, write = prefetch model.Settings r { s with Failed = s.Failed.Remove idea } model.Concepts
            setStudy model r s, write
        | _ -> model, Cmd.none
    | StudyMatched (id, found) ->
        match model with
        | Studying (r, s) when r.Paper.Id = id ->
            let progress =
                { s.Progress with
                    Links = found |> Map.fold (fun links idea c -> if links |> Map.containsKey idea then links else links.Add(idea, c)) s.Progress.Links
                    Matched = DateTime.UtcNow }
            setStudy model r { s with Progress = progress }, saveStudy id progress
        | _ -> model, Cmd.none
    | ToggleStudyPlan ->
        match model with
        | Studying (r, s) -> setStudy model r { s with ShowPlan = not s.ShowPlan; ShowTutor = false }, Cmd.none
        | _ -> model, Cmd.none
    | StudyTeach idea ->
        match model with
        | Studying (r, s) ->
            let p = s.Progress
            // a known idea taught anyway: taught like a new one, and checked again
            let p =
                match p.Done.TryFind idea with
                | Some Study.Outcome.Known
                | Some Study.Outcome.Refreshed -> { p with Done = p.Done.Remove idea; Teach = p.Teach.Add idea }
                | None when p.Links.ContainsKey idea -> { p with Teach = p.Teach.Add idea }
                | _ -> p
            let r, pauseCmd = if r.Playing then pause r else r, Cmd.none
            let s = { s with Progress = p; ShowPlan = false; Running = true }
            let s, cmd = teach model.Settings r s idea
            let s, write = prefetch model.Settings r s model.Concepts
            setStudy model r s, Cmd.batch [ pauseCmd; cmd; write; saveStudy r.Paper.Id p ]
        | _ -> model, Cmd.none
    | StudyTestOut ->
        match model with
        | Studying (r, ({ Now = StudyNow.Teach idea } as s)) ->
            let r, s, cmd = checkIdea model.Settings r { s with Running = true } model.Concepts idea QuizPurpose.TestOut
            setStudy model r s, cmd
        | _ -> model, Cmd.none
    | StudyChoose choice
    | StudySaid choice ->
        match model with
        | Studying (r, ({ Now = StudyNow.Quiz ({ Choice = None } as q); Plan = Some plan } as s)) ->
            let spoken = match msg with StudySaid _ -> true | _ -> false
            stopEar ()
            let now = DateTime.UtcNow
            let idea = q.Idea |> Option.bind plan.Idea
            let before = q.Concept |> Option.orElse (idea |> Option.bind (fun i -> s.Progress.Links.TryFind i.Id)) |> Option.bind model.Concepts.TryFind
            let source: ConceptSource =
                { PaperId = r.Paper.Id; Title = r.Script.Title; Segment = idea |> Option.bind (Study.segmentOf r.Script) |> Option.defaultValue 0 }
            // the lesson's questions join the idea's, for asking again later
            let lessonQuestions =
                idea
                |> Option.bind (fun i -> s.Lessons.TryFind i.Id)
                |> Option.map (fun l -> l.Questions |> List.filter (fun x -> not (s.Progress.Reported.Contains x.Id)))
                |> Option.defaultValue []
            let concept =
                match before, idea with
                | Some c, _ -> Some(c |> Knowledge.addSource source |> Knowledge.addQuestions lessonQuestions)
                | None, Some i -> Some(Knowledge.create now i.Name i.Definition i.Background source lessonQuestions)
                | None, None -> None
            match concept with
            | None -> model, Cmd.none
            | Some concept ->
                let concept = Knowledge.answer model.Settings.Retention now (Knowledge.rating q.Question choice) q.Question.Id concept
                let right = choice = Some q.Question.Correct
                let p = s.Progress
                let p = match idea with Some i -> { p with Links = p.Links.Add(i.Id, concept.Id) } | None -> p
                let p =
                    match q.Purpose, idea with
                    | QuizPurpose.Check, Some i -> { p with Done = p.Done.Add(i.Id, Study.Outcome.Learned); Teach = p.Teach.Remove i.Id }
                    | QuizPurpose.TestOut, Some i when right -> { p with Done = p.Done.Add(i.Id, Study.Outcome.TestedOut); Teach = p.Teach.Remove i.Id }
                    // not known after all: the lesson
                    | QuizPurpose.TestOut, Some i -> { p with Teach = p.Teach.Add i.Id }
                    | QuizPurpose.QuickCheck, Some i when right -> { p with Done = p.Done.Add(i.Id, Study.Outcome.Refreshed) }
                    // forgotten: taught again, here
                    | QuizPurpose.QuickCheck, Some i -> { p with Teach = p.Teach.Add i.Id }
                    | _ -> p
                let q = { q with Choice = Some choice; Spoken = spoken; Concept = Some concept.Id; Before = Some(before, s.Progress) }
                let concepts = model.Concepts.Add(concept.Id, concept)
                let s = { s with Now = StudyNow.Quiz q; Progress = p; Last = Some concept.Id; Ear = Ear.Off; Running = true }
                let outro =
                    match q.Purpose, right with
                    | QuizPurpose.TestOut, true -> "You knew it, so the lesson is skipped."
                    | QuizPurpose.TestOut, false -> "Let's go through it."
                    | QuizPurpose.QuickCheck, true -> "You still know it, so it's skipped here."
                    | QuizPurpose.QuickCheck, false -> "It has faded, so let's go through it again."
                    | _ -> ""
                // what was heard, so a misheard answer is noticed
                let heardBack =
                    match spoken, choice with
                    | true, Some c -> Study.said (sprintf "You said option %d." ((q.Order |> List.tryFindIndex ((=) c) |> Option.defaultValue c) + 1))
                    | true, None -> Study.said "You said you don't know."
                    | false, _ -> []
                let s, cmd = say model.Settings r s (heardBack @ Study.feedbackSaid q.Question q.Order choice @ Study.said outro) Then.Next
                { setStudy model r s with Concepts = concepts }, Cmd.batch [ cmd; saveConcepts concepts; saveStudy r.Paper.Id p ]
        | _ -> model, Cmd.none
    | StudyMisheard ->
        match model with
        | Studying (r, ({ Now = StudyNow.Quiz ({ Choice = Some _; Before = Some (before, progress) } as q) } as s)) ->
            // as if the answer never happened
            let concepts =
                match before, q.Concept with
                | Some c, _ -> model.Concepts.Add(c.Id, c)
                | None, Some c -> model.Concepts.Remove c
                | None, None -> model.Concepts
            let q = { q with Choice = None; Spoken = false; Before = None; Concept = before |> Option.map (fun c -> c.Id) }
            let s = goTo (StudyNow.Quiz q) { s with Progress = progress; Last = None; Running = true }
            let s, cmd = say model.Settings r s (Study.said "Sorry. Which option was it?") Then.Answer
            { setStudy model r s with Concepts = concepts }, Cmd.batch [ cmd; saveConcepts concepts; saveStudy r.Paper.Id progress ]
        | _ -> model, Cmd.none
    | StudyReport ->
        match model with
        | Studying (r, ({ Now = StudyNow.Quiz ({ Choice = Some _; Before = Some (before, progress) } as q) } as s)) ->
            // the answer never happened, and the question is gone for good
            let concepts =
                match before, q.Concept with
                | Some c, _ -> model.Concepts.Add(c.Id, { c with Questions = c.Questions |> List.filter (fun x -> x.Id <> q.Question.Id) })
                | None, Some c -> model.Concepts.Remove c
                | None, None -> model.Concepts
            let progress = { progress with Reported = progress.Reported.Add q.Question.Id }
            let concept = before |> Option.map (fun c -> c.Id)
            let s = { s with Progress = progress; Last = None; Running = true }
            let r, s, cmd =
                match questionFor s concepts q.Idea concept Set.empty with
                | Some next ->
                    let s, cmd = askQuiz model.Settings r s "Sorry about that. Another question." (newQuiz q.Idea concept q.Purpose next)
                    r, s, cmd
                | None -> advance model.Settings r s concepts
            { setStudy model r s with Concepts = concepts }, Cmd.batch [ cmd; saveConcepts concepts; saveStudy r.Paper.Id progress ]
        | _ -> model, Cmd.none
    | StudyAgain ->
        match model with
        | Studying (_, { Now = StudyNow.Listening _ | StudyNow.Starting }) -> update Back15 model
        | Studying (r, ({ Voice = Some v } as s)) when not v.Said.IsEmpty ->
            // what the tutor is saying, from its start
            stopEar ()
            let s, cmd = sayOn model.Settings r { s with Ear = Ear.Off; Tries = 0 } { v with At = 0; Offset = 0 }
            setStudy model r s, cmd
        | _ -> model, Cmd.none
    | StudySkip ->
        match model with
        | Studying (_, { Now = StudyNow.Listening _ | StudyNow.Starting }) -> update Forward15 model
        | Studying (_, { Now = StudyNow.Teach _ }) -> update StudyTestOut model
        | Studying (_, { Now = StudyNow.Quiz { Choice = None } }) -> update (StudyChoose None) model
        | Studying (r, ({ Now = StudyNow.Recap part } as s)) ->
            let s = { s with Progress = { s.Progress with Recaps = s.Progress.Recaps.Add part }; Running = true }
            let r, s, cmd = advance model.Settings r s model.Concepts
            setStudy model r s, cmd
        | Studying (r, ({ Now = StudyNow.Quiz _ } as s)) ->
            let r, s, cmd = advance model.Settings r { s with Running = true } model.Concepts
            setStudy model r s, cmd
        | _ -> model, Cmd.none
    | StudyKey n ->
        match model with
        | Studying (_, { Now = StudyNow.Quiz ({ Choice = None } as q) }) when n >= 1 && n <= q.Order.Length -> update (StudyChoose(Some q.Order.[n - 1])) model
        | _ -> model, Cmd.none
    | StudyClipReady (id, i, path) ->
        match model with
        | Studying (r, ({ Voice = Some v } as s)) when v.Id = id ->
            let v = { v with Ready = v.Ready.Add(i, path) }
            let s = { s with Voice = Some v }
            setStudy model r s, (if v.Playing && v.At = i then playClip model.Settings v else Cmd.none)
        | _ -> model, Cmd.none
    | StudyClipFailed (id, i, message) ->
        match model with
        | Studying (r, ({ Voice = Some v } as s)) when v.Id = id && v.At = i ->
            // the tutor's words stay on screen; its voice is paused until the learner goes on
            let r, s, cmd = hold r s
            setStudy model r { s with Error = Some("The tutor's voice failed: " + message) }, cmd
        | _ -> model, Cmd.none
    | StudyClipEnded (id, i) ->
        match model with
        | Studying (r, ({ Voice = Some v } as s)) when v.Id = id && v.At = i && v.Playing ->
            let v = { v with At = v.At + 1; Offset = 0 }
            if v.At < v.Said.Length then setStudy model r { s with Voice = Some v }, playClip model.Settings v
            else
                let r, s, cmd = voiceDone model.Settings r { s with Voice = Some v } model.Concepts v.Then
                setStudy model r s, cmd
        | _ -> model, Cmd.none
    | StudyListen purpose ->
        match model with
        | Studying (r, s) when (s.Running || purpose = Hearing.Question) && canHear model.Settings ->
            earCancel.Cancel()
            earCancel <- CancellationTokenSource.CreateLinkedTokenSource s.Cancel.Token
            earFinish <- false
            let ct = earCancel.Token
            let settings = model.Settings
            let chime = chimePath ()
            setStudy model r { s with Ear = Ear.Opening purpose; Error = None },
            Cmd.ofEffect (fun dispatch ->
                let start () = if not ct.IsCancellationRequested then Task.Run(fun () -> hearing settings purpose ct dispatch) |> ignore
                try
                    if not (File.Exists chime) then Wav.writeChime chime
                    (platform ()).Player.Play(chime, 0, 1.0, start, (fun _ -> start ()))
                with _ -> start ())
        | _ -> model, Cmd.none
    | StudyEarOpened purpose ->
        match model with
        | Studying (r, ({ Ear = Ear.Opening p } as s)) when p = purpose -> setStudy model r { s with Ear = Ear.Open(purpose, 0.0, false) }, Cmd.none
        | _ -> model, Cmd.none
    | StudyEarLevel level ->
        match model with
        | Studying (r, ({ Ear = Ear.Open (p, _, spoke) } as s)) -> setStudy model r { s with Ear = Ear.Open(p, level, spoke || level > 0.1) }, Cmd.none
        | _ -> model, Cmd.none
    | StudyEarTranscribing purpose ->
        match model with
        | Studying (r, s) when s.Ear <> Ear.Off -> setStudy model r { s with Ear = Ear.Transcribing purpose }, Cmd.none
        | _ -> model, Cmd.none
    | StudyHeard (purpose, result) ->
        match model with
        | Studying (r, s) when s.Ear <> Ear.Off ->
            let s = { s with Ear = Ear.Off; Heard = (match result with Ok t -> t | Error _ -> s.Heard) }
            match purpose, result, s.Now with
            | _, Error e, _ -> setStudy model r { s with Error = Some e }, Cmd.none
            | Hearing.Answer, Ok text, StudyNow.Quiz ({ Choice = None } as q) ->
                let heard = if text = "" then Study.Heard.Unclear else Study.heardAnswer (q.Order |> List.map (fun i -> q.Question.Options.[i])) text
                match heard with
                | Study.Heard.Option pos -> update (StudySaid(Some q.Order.[pos])) (setStudy model r s)
                | Study.Heard.DontKnow -> update (StudySaid None) (setStudy model r s)
                | Study.Heard.Unclear when s.Tries = 0 ->
                    let numbers = q.Order |> List.mapi (fun i _ -> string (i + 1))
                    let prompt =
                        (if text = "" then "I didn't hear an answer. " else "Sorry, I didn't catch that. ")
                        + sprintf "Say option %s or %s, or I don't know." (String.Join(", ", numbers |> List.take (numbers.Length - 1))) (List.last numbers)
                    let s, cmd = say model.Settings r { s with Tries = 1 } (Study.said prompt) Then.Answer
                    setStudy model r s, cmd
                | Study.Heard.Unclear ->
                    // waiting for a tap, or Continue to be asked again
                    let r, s, cmd = hold r { s with Tries = s.Tries + 1 }
                    setStudy model r s, cmd
            | Hearing.Explanation, Ok "", StudyNow.Recap part ->
                let s = { s with Progress = { s.Progress with Recaps = s.Progress.Recaps.Add part } }
                let s, cmd = say model.Settings r s (Study.said "Let's go on.") Then.Next
                setStudy model r s, cmd
            | Hearing.Explanation, Ok text, StudyNow.Recap _ -> update SubmitRecap (setStudy model r { s with RecapInput = text })
            | Hearing.Question, Ok text, _ when text <> "" ->
                // said after typing part of it: the whole question
                let question = if s.Input.Trim() = "" then text else s.Input.Trim() + " " + text
                update (StudyAsk(question, false)) (setStudy model r s)
            | _ -> setStudy model r s, Cmd.none
        | _ -> model, Cmd.none
    | ToggleTutor ->
        match model with
        | Studying (r, s) when s.ShowTutor ->
            // back to what the tutor was saying before the questions
            let r, s, stop = hold r s
            let s = { s with ShowTutor = false; Voice = (match s.Aside with Some v -> Some v | None -> s.Voice); Aside = None }
            let r, s, cmd = resume model.Settings r s model.Concepts
            setStudy model r s, Cmd.batch [ stop; cmd ]
        | Studying (r, s) ->
            // the tutor waits; the microphone opens only when its button is pressed
            let r, s, cmd = hold r s
            let s = { s with ShowTutor = true; ShowPlan = false }
            setStudy model r s, cmd
        | _ -> model, Cmd.none
    | StudyMic ->
        match model with
        | Studying (_, { Ear = Ear.Open _ }) ->
            earFinish <- true
            model, Cmd.none
        | Studying (r, s) when s.Ear = Ear.Off && s.Pending.IsNone ->
            if not (canHear model.Settings) then
                setStudy model r { s with Error = Some "Asking aloud needs a microphone and a Mistral API key (Settings), and Answer aloud on." }, Cmd.none
            else
                // the tutor stops talking to listen
                let r, s, cmd = hold r s
                setStudy model r s, Cmd.batch [ cmd; Cmd.ofMsg (StudyListen Hearing.Question) ]
        | _ -> model, Cmd.none
    | StudyAsk (question, quick) ->
        match model with
        | Studying (r, s) when s.Pending.IsNone && question.Trim() <> "" ->
            if not (Settings.hasKey model.Settings) then setStudy model r { s with Error = Some "Asking the tutor needs a Mistral API key (Settings)." }, Cmd.none
            else
                match momentOf s model.Concepts with
                | Some m ->
                    stopEar ()
                    let s, cmd = askTutor model.Settings r { s with Ear = Ear.Off; ShowTutor = true } m (question.Trim()) quick
                    setStudy model r s, cmd
                | None -> model, Cmd.none
        | _ -> model, Cmd.none
    | StudyAskText (askId, text) ->
        match model with
        | Studying (r, ({ Pending = Some (id, q, _) } as s)) when id = askId -> setStudy model r { s with Pending = Some(id, q, text) }, Cmd.none
        | _ -> model, Cmd.none
    | StudyAnswered (askId, result) ->
        match model with
        | Studying (r, ({ Pending = Some (id, q, _) } as s)) when id = askId ->
            match result with
            | Ok reply ->
                let s =
                    { s with
                        Pending = None
                        Chat = s.Chat @ [ { Question = q; Answer = reply.Answer } ]
                        Followups = reply.Followups
                        Running = true
                        Aside = (match s.Aside with Some v -> Some v | None -> s.Voice |> Option.map (fun v -> { v with Playing = false })) }
                // the answer is said; the session waits in the panel until the learner goes on
                let s, cmd = say model.Settings r s (Study.said reply.Answer) Then.Wait
                setStudy model r s, cmd
            | Error e -> setStudy model r { s with Pending = None; Error = (if e = "Cancelled." then None else Some e) }, Cmd.none
        | _ -> model, Cmd.none
    | SetStudyInput text ->
        match model with
        | Studying (r, s) -> setStudy model r { s with Input = text }, Cmd.none
        | _ -> model, Cmd.none
    | SendStudyInput ->
        match model with
        | Studying (_, s) when s.Input.Trim() <> "" -> update (StudyAsk(s.Input, false)) model
        | _ -> model, Cmd.none
    | SetRecapInput text ->
        match model with
        | Studying (r, s) -> setStudy model r { s with RecapInput = text }, Cmd.none
        | _ -> model, Cmd.none
    | SubmitRecap ->
        match model with
        | Studying (r, ({ Now = StudyNow.Recap part; Plan = Some plan } as s)) when not s.Grading && s.RecapInput.Trim() <> "" ->
            if not (Settings.hasKey model.Settings) then setStudy model r { s with Error = Some "Feedback on your answer needs a Mistral API key (Settings)." }, Cmd.none
            else
                stopEar ()
                voiceCancel.Cancel()
                let s = { s with Grading = true; Feedback = None; Error = None; Ear = Ear.Off; Voice = None; Running = true }
                setStudy model r s, Cmd.batch [ stopAudio; gradeRecap model.Settings r s plan part s.RecapInput ]
        | _ -> model, Cmd.none
    | RecapText f ->
        match model with
        | Studying (r, s) when s.Grading -> setStudy model r { s with Feedback = Some f }, Cmd.none
        | _ -> model, Cmd.none
    | RecapDone result ->
        match model with
        | Studying (r, ({ Now = StudyNow.Recap part } as s)) when s.Grading ->
            match result with
            | Ok f ->
                let s = { s with Grading = false; Feedback = Some f; Progress = { s.Progress with Recaps = s.Progress.Recaps.Add part } }
                let verdict =
                    match f.Verdict with
                    | Some Study.Verdict.GotIt -> "You've got it. "
                    | Some Study.Verdict.Partly -> "Partly there. "
                    | Some Study.Verdict.NotYet -> "Not yet. "
                    | None -> ""
                let s, cmd = say model.Settings r s (Study.said (verdict + f.Text)) Then.Next
                setStudy model r s, Cmd.batch [ cmd; saveStudy r.Paper.Id s.Progress ]
            | Error e -> setStudy model r { s with Grading = false; Feedback = None; Error = (if e = "Cancelled." then None else Some e) }, Cmd.none
        | _ -> model, Cmd.none
    | SetStudy on ->
        let settings = { model.Settings with Study = on }
        { model with Settings = settings }, saveSettings settings
    | SetAnswerAloud on ->
        let settings = { model.Settings with AnswerAloud = on }
        { model with Settings = settings }, saveSettings settings

    // ----- settings
    | SetShowSettings show ->
        let model = { model with ShowSettings = show }
        if show then model, (if model.Voices.IsEmpty && Settings.hasKey model.Settings then Cmd.ofMsg LoadVoices else Cmd.none)
        else
            // a different voice means a different audio cache: restart synthesis for the open paper
            match model.Screen with
            | Screen.Reader r when r.VoiceKey <> Settings.voiceKey model.Settings ->
                let wasPlaying = r.Playing
                let r, stop = pause r
                let r =
                    { r with
                        VoiceKey = Settings.voiceKey model.Settings
                        Offset = 0
                        Error = None
                        Durations = Synth.cachedDurations (fun i -> (paths ()).Audio(r.Paper.Id, Settings.voiceKey model.Settings, i)) r.Script.Segments.Length }
                let r, playCmd = if wasPlaying then play r model.Settings else r, Cmd.none
                { model with Screen = Screen.Reader r }, Cmd.batch [ stop; startSynth r model.Settings; playCmd ]
            | _ -> model, Cmd.none
    | SetApiKey key ->
        let settings = { model.Settings with MistralApiKey = key.Trim() }
        { model with Settings = settings; Voices = (if key.Trim() = model.Settings.MistralApiKey then model.Voices else []) }, saveSettings settings
    | SetNarration on ->
        let settings = { model.Settings with UseMistralNarration = on }
        { model with Settings = settings }, saveSettings settings
    | SetNarrationModel m ->
        let settings = { model.Settings with NarrationModel = (if String.IsNullOrWhiteSpace m then Settings.defaults.NarrationModel else m.Trim()) }
        { model with Settings = settings }, saveSettings settings
    | SetStopAtFigures on ->
        let settings = { model.Settings with StopAtFigures = on }
        { model with Settings = settings }, saveSettings settings
    | SetWalking on ->
        let settings = { model.Settings with WalkingMode = on }
        { model with Settings = settings }, saveSettings settings
    | SetTurnSideways on ->
        let settings = { model.Settings with TurnSideways = on }
        { model with Settings = settings }, saveSettings settings
    | SetStopAtEquations on ->
        let settings = { model.Settings with StopAtEquations = on }
        { model with Settings = settings }, saveSettings settings
    | SetMistralVoice on ->
        let settings = { model.Settings with UseMistralVoice = on }
        { model with Settings = settings }, saveSettings settings
    | UsePhoneVoice ->
        let settings = { model.Settings with UseMistralVoice = false }
        let model, cmd = update (SetShowSettings false) { model with Settings = settings; ShowSettings = true }
        model, Cmd.batch [ saveSettings settings; cmd ]
    | PickVoice v ->
        let settings = { model.Settings with VoiceId = v.Id; VoiceName = v.Name }
        { model with Settings = settings }, saveSettings settings
    | LoadVoices ->
        if not (Settings.hasKey model.Settings) then { model with VoicesStatus = Some "Enter an API key first." }, Cmd.none
        else
            let key = model.Settings.MistralApiKey
            { model with VoicesStatus = Some "Loading voices…" },
            runTask (fun () -> Mistral.listVoices key CancellationToken.None) (Ok >> VoicesLoaded) (errorText >> Error >> VoicesLoaded)
    | VoicesLoaded (Ok voices) -> { model with Voices = voices; VoicesStatus = None }, Cmd.none
    | VoicesLoaded (Error e) -> { model with VoicesStatus = Some e }, Cmd.none

    | Resized (width, height) ->
        let w, h = model.Viewport
        if abs (width - w) < 0.5 && abs (height - h) < 0.5 then model, Cmd.none
        else { model with Viewport = (width, height) }, Cmd.none

    // ----- Android back button
    | BackPressed ->
        if model.ShowSettings then update (SetShowSettings false) model
        elif model.Review.IsSome then update CloseReview model
        else
            match model.Screen with
            | Screen.Reader r when r.Zoom.IsSome -> update ToggleZoom model
            | Screen.Reader { Study = Some s } when s.ShowTutor -> update ToggleTutor model
            | Screen.Reader { Study = Some s } when s.ShowPlan -> update ToggleStudyPlan model
            | Screen.Reader r when r.Help.IsSome -> update (CloseHelp false) model
            | Screen.Reader r when r.Cards.IsSome -> update (CloseCards false) model
            | Screen.Reader r when r.ShowOutline -> update ToggleOutline model
            | Screen.Reader r when r.ShowEquations -> update ToggleEquations model
            // a stray back press on a walk leaves the simple player, it doesn't stop the paper
            | Screen.Reader _ when model.Settings.WalkingMode -> update (SetWalking false) model
            | Screen.Reader _ -> update CloseReader model
            | Screen.Importing _ -> update CancelImport model
            | Screen.Discover -> update CloseDiscover model
            | Screen.Learn -> update CloseLearn model
            | Screen.Library -> model, Cmd.none

/// True when the back button has something to close inside the app.
let canGoBack (model: Model) =
    model.ShowSettings || model.Review.IsSome || (match model.Screen with Screen.Library -> false | _ -> true)

let subscriptions (model: Model) : Sub<Msg> =
    match model.Screen with
    | Screen.Reader r when r.Playing && not r.Waiting ->
        [ [ "tick" ],
          fun (dispatch: Dispatch<Msg>) ->
              let timer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 250.0)
              timer.Tick.Add(fun _ -> dispatch (Tick (platform ()).Player.PositionMs))
              timer.Start()
              { new IDisposable with member _.Dispose() = timer.Stop() } ]
    | _ -> []
