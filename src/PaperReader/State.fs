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
      Zoomed: bool }

[<RequireQualifiedAccess>]
type Screen =
    | Library
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
      ConfirmDelete: string option }

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
    | ToggleZoom
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
        | None -> dispatch (ClipFailed(r.Paper.Id, r.Current, "This phone has no text-to-speech engine. Add a Mistral API key in Settings."))
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

let private keepScreenOn (on: bool) : Cmd<Msg> =
    Cmd.ofEffect (fun _ -> (platform ()).KeepScreenOn on)

let private saveProgress (r: ReaderState) : Cmd<Msg> =
    Cmd.ofEffect (fun _ ->
        try Store.saveMeta (paths ()) { r.Paper with LastSegment = r.Current; SegmentCount = r.Script.Segments.Length }
        with _ -> ())

let private saveSettings (s: Settings) : Cmd<Msg> =
    Cmd.ofEffect (fun _ -> try Store.saveSettings (paths ()) s with _ -> ())

let private loadLibrary () =
    try Store.library (paths ()) with _ -> []

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
    | :? Net.Http.HttpRequestException -> "Couldn't reach Mistral. Check the internet connection."
    | e -> e.Message

// ---------------------------------------------------------------------------------------------
// Playback
// ---------------------------------------------------------------------------------------------

/// Starts (or waits for) the clip at the reader's current position.
let private play (r: ReaderState) (settings: Settings) : ReaderState * Cmd<Msg> =
    let gen = r.Generation + 1
    let r = { r with Playing = true; Finished = false; Generation = gen }
    if r.Durations.ContainsKey r.Current then
        let path = (paths ()).Audio(r.Paper.Id, r.VoiceKey, r.Current)
        let start =
            Cmd.ofEffect (fun dispatch ->
                (platform ()).Player.Play(path, r.Offset, settings.Speed, (fun () -> dispatch (ClipEnded gen)), (fun e -> dispatch (PlayerFailed e))))
        { r with Waiting = false }, Cmd.batch [ start; moveSynth r.Current; keepScreenOn true ]
    else
        { r with Waiting = true }, Cmd.batch [ stopAudio; moveSynth r.Current; keepScreenOn true ]

let private pause (r: ReaderState) : ReaderState * Cmd<Msg> =
    let offset = if r.Playing && not r.Waiting then (platform ()).Player.PositionMs else r.Offset
    { r with Playing = false; Waiting = false; Offset = offset; Generation = r.Generation + 1 },
    Cmd.batch [ stopAudio; keepScreenOn false; saveProgress r ]

/// Moves to a position, keeping the play/pause state.
let private seek (r: ReaderState) (settings: Settings) (pos: Timeline.Position) : ReaderState * Cmd<Msg> =
    let r = { r with Current = pos.Segment; Offset = pos.OffsetMs; Finished = false; Error = None }
    if r.Playing then play r settings
    else r, Cmd.batch [ moveSynth r.Current; saveProgress r ]

let private currentPosition (r: ReaderState) : Timeline.Position =
    let offset = if r.Playing && not r.Waiting then (platform ()).Player.PositionMs else r.Offset
    { Segment = r.Current; OffsetMs = offset }

// ---------------------------------------------------------------------------------------------
// Init and update
// ---------------------------------------------------------------------------------------------

let init () : Model * Cmd<Msg> =
    let settings = try Store.loadSettings (paths ()) with _ -> Settings.defaults
    { Screen = Screen.Library
      Papers = loadLibrary ()
      Settings = settings
      ShowSettings = false
      Voices = []
      VoicesStatus = None
      Notice = None
      ConfirmDelete = None },
    Cmd.ofEffect (fun dispatch ->
        (platform ()).SetIncomingPdfHandler(fun (path, name) -> dispatch (ImportFile(path, name))))

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
                | Screen.Reader r -> Cmd.batch [ snd (pause r); Cmd.ofEffect (fun _ -> stopSynth ()) ]
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
        | Screen.Importing s, Ok (paper, script, warning) when s.Id = importId ->
            let model = { model with Papers = loadLibrary (); Notice = warning |> Option.map (fun w -> "Some parts use the offline narration: " + w) }
            model, Cmd.ofMsg (PaperLoaded(Ok(paper, script, Map.empty)))
        | Screen.Importing s, Error e when s.Id = importId -> { model with Screen = Screen.Library; Notice = Some e }, Cmd.none
        | _ -> model, Cmd.none // cancelled meanwhile
    | AskDelete id -> { model with ConfirmDelete = Some id }, Cmd.none
    | DeletePaper id ->
        (try Store.deletePaper (paths ()) id with _ -> ())
        { model with Papers = loadLibrary (); ConfirmDelete = None }, Cmd.none

    // ----- reader
    | OpenPaper paper ->
        let model = { model with ConfirmDelete = None }
        let p = paths ()
        let key = Settings.voiceKey model.Settings
        let load () =
            task {
                match Store.loadScript p paper.Id with
                | None -> return failwith "This paper's cache is from an older version. Remove it and add the PDF again."
                | Some script ->
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
              Zoomed = false }
        let r, playCmd = play r model.Settings
        { model with Screen = Screen.Reader r }, Cmd.batch [ startSynth r model.Settings; playCmd ]
    | CloseReader ->
        match model.Screen with
        | Screen.Reader r ->
            let _, cmd = pause r
            { model with Screen = Screen.Library; Papers = loadLibrary () |> List.map (fun p -> if p.Id = r.Paper.Id then { p with LastSegment = r.Current } else p) },
            Cmd.batch [ cmd; Cmd.ofEffect (fun _ -> stopSynth ()) ]
        | _ -> model, Cmd.none
    | TogglePlay ->
        withReader model (fun r ->
            if r.Playing then pause r
            elif r.Finished then play { r with Current = 0; Offset = 0 } model.Settings
            else play r model.Settings)
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
            let r = { r with ShowOutline = false }
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
            elif r.Current + 1 < r.Script.Segments.Length then
                let r = { r with Current = r.Current + 1; Offset = 0 }
                let r, cmd = play r model.Settings
                r, Cmd.batch [ cmd; saveProgress r ]
            else
                { r with Playing = false; Waiting = false; Finished = true; Offset = 0 },
                Cmd.batch [ keepScreenOn false; saveProgress { r with Current = 0 } ])
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
        withReader model (fun r -> { r with Playing = false; Waiting = false; Error = Some("Playback failed: " + message) }, keepScreenOn false)
    | ToggleOutline -> withReader model (fun r -> { r with ShowOutline = not r.ShowOutline; Zoomed = false }, Cmd.none)
    | ToggleZoom -> withReader model (fun r -> { r with Zoomed = not r.Zoomed }, Cmd.none)

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
        else
            match model.Screen with
            | Screen.Reader r when r.Zoomed -> update ToggleZoom model
            | Screen.Reader r when r.ShowOutline -> update ToggleOutline model
            | Screen.Reader _ -> update CloseReader model
            | Screen.Importing _ -> update CancelImport model
            | Screen.Library -> model, Cmd.none

/// True when the back button has something to close inside the app.
let canGoBack (model: Model) =
    model.ShowSettings || (match model.Screen with Screen.Library -> false | _ -> true)

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
