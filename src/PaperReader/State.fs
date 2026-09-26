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

/// A review session: cards shown one at a time until none is due.
type ReviewState =
    { /// The paper reviewed, or None for every paper.
      Scope: string option
      /// Cards to show, the current one first (paper id, card).
      Queue: (string * Card) list
      Revealed: bool
      /// Answers given, and how many of them were not Again.
      Answered: int
      Remembered: int
      /// Cards in the session when it started (learning steps shown again aren't counted).
      Total: int }

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
      /// The visual shown full size, if any.
      Zoom: string option
      /// Where "stop at equations" pauses: segment index -> the equation it has just read and explained.
      Stops: Map<int, string>
      /// The equation kept on screen after such a stop, until the listener continues.
      Held: string option
      /// The Ask panel, when open.
      Help: HelpState option
      /// The Remember panel, when open.
      Cards: CardsPanel option }

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
      LearnOpen: string option }

type Msg =
    | OpenPdf
    | PdfPicked of (string * string) option
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
    /// Shows the equation on screen full size, or closes the full-size view.
    | ToggleZoom
    | ZoomVisual of string
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
    /// Goes back a few sentences and carries on listening.
    | HelpReplay
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
      LearnOpen = None },
    Cmd.ofEffect (fun dispatch ->
        (platform ()).SetIncomingPdfHandler(fun (path, name) -> dispatch (ImportFile(path, name)))
        (platform ()).SetRemoteHandler(fun play -> dispatch (Remote play)))

let private withReader (model: Model) (f: ReaderState -> ReaderState * Cmd<Msg>) : Model * Cmd<Msg> =
    match model.Screen with
    | Screen.Reader r ->
        let r, cmd = f r
        { model with Screen = Screen.Reader r }, cmd
    | _ -> model, Cmd.none

let private pickPdf () : Task<(string * string) option> =
    task {
        match Services.topLevel with
        | None -> return None
        | Some top ->
            let options =
                Avalonia.Platform.Storage.FilePickerOpenOptions(
                    Title = "Open a paper",
                    AllowMultiple = false,
                    FileTypeFilter =
                        [| Avalonia.Platform.Storage.FilePickerFileType("PDF", Patterns = [| "*.pdf" |], MimeTypes = [| "application/pdf" |]) |])
            let! files = Dispatcher.UIThread.InvokeAsync<Collections.Generic.IReadOnlyList<Avalonia.Platform.Storage.IStorageFile>>(fun () -> top.StorageProvider.OpenFilePickerAsync options)
            if files.Count = 0 then return None
            else
                let file = files.[0]
                let incoming = Path.Combine((platform ()).DataDir, "incoming")
                Directory.CreateDirectory incoming |> ignore
                let dest = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".pdf")
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
                            | None -> failwith "This paper's cache is from an older version. Remove it and add the PDF again."
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
    | OpenPdf ->
        { model with Notice = None },
        runTask pickPdf PdfPicked (fun e -> PdfPicked None)
    | PdfPicked None -> model, Cmd.none
    | PdfPicked (Some (path, name)) -> model, Cmd.ofMsg (ImportFile(path, name))
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
                            | None -> dispatch (ImportFinished(importId, Error "This paper's cache is from an older version. Remove it and add the PDF again."))
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
                | None -> return failwith "This paper's cache is from an older version. Remove it and add the PDF again."
                | Some script ->
                    do! (platform ()).Restore(p.AudioDir(paper.Id, key))
                    let durations = Synth.cachedDurations (fun i -> p.Audio(paper.Id, key, i)) script.Segments.Length
                    return paper, script, durations
            }
        model, runTask load (Ok >> PaperLoaded) (errorText >> Error >> PaperLoaded)
    | PaperLoaded (Error e) -> { model with Notice = Some e }, Cmd.none
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
              Cards = None }
        let r, playCmd = play r model.Settings
        { model with Screen = Screen.Reader r }, Cmd.batch [ startSynth r model.Settings; playCmd ]
    | CloseReader ->
        match model.Screen with
        | Screen.Reader r ->
            let _, cmd = pause r
            { model with Screen = Screen.Library; Papers = loadLibrary () |> List.map (fun p -> if p.Id = r.Paper.Id then { p with LastSegment = r.Current } else p) },
            Cmd.batch [ cmd; Cmd.ofEffect (fun _ -> stopSynth (); (platform ()).EndPlayback()) ]
        | _ -> model, Cmd.none
    | TogglePlay when (match model.Screen with Screen.Reader r -> r.Help.IsSome | _ -> false) -> update (CloseHelp true) model
    | TogglePlay when (match model.Screen with Screen.Reader r -> r.Cards.IsSome | _ -> false) -> update (CloseCards true) model
    | TogglePlay ->
        withReader model (fun r ->
            if r.Playing then pause r
            elif r.Finished then play { r with Current = 0; Offset = 0 } model.Settings
            else play r model.Settings)
    | Remote wanted ->
        withReader model (fun r ->
            if wanted && not r.Playing then
                if r.Finished then play { r with Current = 0; Offset = 0 } model.Settings else play r model.Settings
            elif not wanted && r.Playing then pause r
            else r, Cmd.none)
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
            let r = { r with ShowOutline = false; ShowEquations = false; Zoom = None; Help = None; Cards = None }
            let r, cmd = seek r model.Settings { Segment = i; OffsetMs = 0 }
            if r.Playing then r, cmd else play r model.Settings)
    | CycleSpeed ->
        let i = speeds |> Array.tryFindIndex (fun s -> abs (s - model.Settings.Speed) < 0.01) |> Option.defaultValue 1
        let speed = speeds.[(i + 1) % speeds.Length]
        let settings = { model.Settings with Speed = speed }
        { model with Settings = settings },
        Cmd.batch [ saveSettings settings; Cmd.ofEffect (fun _ -> (platform ()).Player.SetSpeed speed) ]
    | Tick position ->
        withReader model (fun r -> (if r.Playing && not r.Waiting then { r with Offset = position } else r), Cmd.none)
    | ClipEnded gen ->
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
    | HelpReplay ->
        match model.Screen with
        | Screen.Reader ({ Help = Some h } as r) ->
            // back to the start of the last few sentences, then listen again
            let target = max 0 (h.Position - 2)
            let model, cmd = update (CloseHelp false) { model with Screen = Screen.Reader { r with Help = Some { h with ResumeOnClose = false } } }
            let model, cmd2 = update (JumpToSegment target) model
            model, Cmd.batch [ cmd; cmd2 ]
        | _ -> model, Cmd.none
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
            let model, _ = withHelp model (fun _ h -> { h with Mic = Mic.Idle }, Cmd.none)
            update (AskHelp(Help.Ask.Free text, true)) model
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
        { model with Screen = Screen.Learn; Notice = None; ConfirmDelete = None; Decks = loadDecks model.Papers }, Cmd.none
    | CloseLearn -> { model with Screen = Screen.Library; MakeError = None }, Cmd.none
    | ToggleDeck id -> { model with LearnOpen = (if model.LearnOpen = Some id then None else Some id) }, Cmd.none
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
        |> fun (m, cmd) -> { m with MakeError = None }, cmd
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
        let review =
            model.Review
            |> Option.map (fun rv ->
                match rv.Queue with
                | (_, c) :: rest when c.Id = cardId -> { rv with Queue = rest; Revealed = false }
                | q -> { rv with Queue = q |> List.filter (fun (_, c) -> c.Id <> cardId) })
        { model with Made = made; Review = review }, save
    | StartReview scope ->
        let decks =
            match scope with
            | Some id -> [ id, deckOf model id ]
            | None -> Map.toList model.Decks
        match Cards.dueQueue DateTime.UtcNow decks with
        | [] -> { model with Notice = Some "Nothing to review right now." }, Cmd.none
        | queue ->
            let model, pauseCmd = withReader model (fun r -> if r.Playing then pause r else r, Cmd.none)
            { model with Review = Some { Scope = scope; Queue = queue; Revealed = false; Answered = 0; Remembered = 0; Total = queue.Length } },
            pauseCmd
    | ShowAnswer -> withReview model (fun rv -> { rv with Revealed = true }, Cmd.none)
    | RateCard rating ->
        match model.Review with
        | Some ({ Revealed = true; Queue = (paperId, c) :: rest } as rv) ->
            let card = { c with Memory = Fsrs.review model.Settings.Retention DateTime.UtcNow c.Id c.Memory rating }
            let model, save = setDeck model paperId (deckOf model paperId |> List.map (fun x -> if x.Id = c.Id then card else x))
            // a card still in its short steps comes back later in the session
            let again = card.Memory.Stage = CardStage.Learning || card.Memory.Stage = CardStage.Relearning
            let rv =
                { rv with
                    Queue = (if again then rest @ [ paperId, card ] else rest)
                    Revealed = false
                    Answered = rv.Answered + 1
                    Remembered = rv.Remembered + (if rating = Fsrs.Rating.Again then 0 else 1) }
            { model with Review = Some rv }, save
        | _ -> model, Cmd.none
    | ReviewNext ->
        match model.Review with
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

    // ----- Android back button
    | BackPressed ->
        if model.ShowSettings then update (SetShowSettings false) model
        elif model.Review.IsSome then update CloseReview model
        else
            match model.Screen with
            | Screen.Reader r when r.Zoom.IsSome -> update ToggleZoom model
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
