namespace PaperReader.Android

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Android.Content
open Android.Graphics
open Android.Graphics.Pdf
open Android.Media
open Android.OS
open Android.Speech.Tts
open Android.Views
open PaperReader
open PaperReader.Core

// ---------------------------------------------------------------------------------------------
// Audio playback
// ---------------------------------------------------------------------------------------------

/// Plays one WAV clip at a time with MediaPlayer; speed changes keep the pitch.
type AndroidPlayer() =
    let mutable current: MediaPlayer = null
    let mutable speed = 1.0f

    let release () =
        match current with
        | null -> ()
        | p ->
            current <- null
            try
                if p.IsPlaying then p.Stop()
            with _ -> ()
            p.Release()

    let applySpeed (p: MediaPlayer) =
        // setting playback params on a prepared player also starts it
        p.PlaybackParams <- p.PlaybackParams.SetSpeed(speed).SetPitch(1.0f)

    interface IAudioPlayer with
        member _.Play(path, startMs, playbackSpeed, onEnded, onError) =
            release ()
            speed <- float32 playbackSpeed
            let p = new MediaPlayer()
            try
                p.SetAudioAttributes(
                    AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Speech).Build())
                p.SetDataSource path
                p.Prepare()
                p.Completion.Add(fun _ -> if obj.ReferenceEquals(current, p) then onEnded ())
                p.Error.Add(fun e ->
                    if obj.ReferenceEquals(current, p) then
                        e.Handled <- true
                        onError (sprintf "media error %A" e.What))
                if startMs > 0 then p.SeekTo(startMs)
                current <- p
                applySpeed p
                p.Start()
            with e ->
                p.Release()
                if obj.ReferenceEquals(current, p) then current <- null
                onError e.Message

        member _.Stop() = release ()

        member _.PositionMs =
            match current with
            | null -> 0
            | p -> try p.CurrentPosition with _ -> 0

        member _.SetSpeed(s) =
            speed <- float32 s
            match current with
            | null -> ()
            | p -> try (if p.IsPlaying then applySpeed p) with _ -> ()

// ---------------------------------------------------------------------------------------------
// The phone's own text-to-speech, rendered to WAV files so it is cached like Mistral audio
// ---------------------------------------------------------------------------------------------

type private InitListener(onInit: OperationResult -> unit) =
    inherit Java.Lang.Object()
    interface TextToSpeech.IOnInitListener with
        member _.OnInit(status) = onInit status

type private UtteranceListener(finished: string -> string option -> unit) =
    inherit UtteranceProgressListener()
    override _.OnStart(_: string) = ()
    override _.OnDone(id: string) = finished id None
    [<Obsolete>]
    override _.OnError(id: string) = finished id (Some "the phone's text-to-speech failed")
    override _.OnError(id: string, code: TextToSpeechError) = finished id (Some(sprintf "the phone's text-to-speech failed (%A)" code))

type SystemSpeech(context: Context) =
    let ready = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
    let pending = ConcurrentDictionary<string, TaskCompletionSource<unit>>()
    let gate = new SemaphoreSlim(1)
    let mutable tts: TextToSpeech = null

    do
        tts <-
            new TextToSpeech(
                context,
                new InitListener(fun status ->
                    if status = OperationResult.Success then
                        try
                            let r = tts.SetLanguage(Java.Util.Locale.Us)
                            if r = LanguageAvailableResult.MissingData || r = LanguageAvailableResult.NotSupported then
                                tts.SetLanguage(Java.Util.Locale.Default) |> ignore
                        with _ -> ()
                        tts.SetOnUtteranceProgressListener(
                            new UtteranceListener(fun id error ->
                                match pending.TryRemove id with
                                | true, t ->
                                    match error with
                                    | None -> t.TrySetResult() |> ignore
                                    | Some e -> t.TrySetException(Exception e) |> ignore
                                | _ -> ()))
                        |> ignore
                        ready.TrySetResult true |> ignore
                    else ready.TrySetResult false |> ignore))

    interface Synth.ISpeechEngine with
        member _.Key = "system"
        member _.Parallelism = 1

        member _.Synthesize(text, ct) =
            task {
                let! ok = ready.Task.WaitAsync(TimeSpan.FromSeconds 15.0, ct)
                if not ok then failwith "The phone's text-to-speech engine is not available."
                do! gate.WaitAsync(ct)
                try
                    let id = Guid.NewGuid().ToString("N")
                    let file = Path.Combine(context.CacheDir.AbsolutePath, id + ".wav")
                    let done' = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                    pending.[id] <- done'
                    let limit = TextToSpeech.MaxSpeechInputLength - 1
                    let text = if text.Length > limit then text.Substring(0, limit) else text
                    let result = tts.SynthesizeToFile(text, Bundle(), new Java.IO.File(file), id)
                    if result <> OperationResult.Success then
                        pending.TryRemove id |> ignore
                        failwith "The phone's text-to-speech refused the text."
                    do! done'.Task.WaitAsync(TimeSpan.FromMinutes 2.0, ct)
                    let bytes = File.ReadAllBytes file
                    File.Delete file
                    return bytes
                finally
                    gate.Release() |> ignore
            }

// ---------------------------------------------------------------------------------------------
// Cropping the math out of the PDF
// ---------------------------------------------------------------------------------------------

module Crops =
    let private gapPx = 24

    /// Renders a visual's regions at about 3x the paper's size (216 dpi), stacked top to bottom with a thin
    /// divider between parts. Very wide regions are scaled down to stay under 2400 px.
    let render (pdfPath: string) (crops: (Visual * string) list) (progress: int -> unit) (ct: CancellationToken) =
        use fd = ParcelFileDescriptor.Open(new Java.IO.File(pdfPath), ParcelFileMode.ReadOnly)
        use renderer = new PdfRenderer(fd)
        let mutable doneCount = 0
        use divider = new Paint(Color = Color.Rgb(224, 226, 230), StrokeWidth = 3.0f)
        for (page, items) in crops |> List.groupBy (fun (v, _) -> v.Page) do
            if page < renderer.PageCount then
                use p = renderer.OpenPage page
                for (v, output) in items do
                    ct.ThrowIfCancellationRequested()
                    if not (File.Exists output) then
                        let widest = v.Parts |> Array.map (fun r -> r.W) |> Array.max
                        let scale = min 3.0 (2400.0 / max 1.0 widest)
                        let size (r: PageRect) = max 1 (int (r.W * scale)), max 1 (int (r.H * scale))
                        let w = v.Parts |> Array.map (size >> fst) |> Array.max
                        let h = (v.Parts |> Array.sumBy (size >> snd)) + gapPx * (v.Parts.Length - 1)
                        use bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888)
                        bmp.EraseColor(Color.White.ToArgb())
                        use canvas = new Canvas(bmp)
                        let mutable top = 0
                        for k in 0 .. v.Parts.Length - 1 do
                            let r = v.Parts.[k]
                            let pw, ph = size r
                            if k > 0 then
                                let y = float32 (top - gapPx / 2)
                                canvas.DrawLine(0.0f, y, float32 w, y, divider)
                            use m = new Matrix()
                            m.PostTranslate(float32 -r.X, float32 -r.Y) |> ignore
                            m.PostScale(float32 scale, float32 scale) |> ignore
                            m.PostTranslate(0.0f, float32 top) |> ignore
                            p.Render(bmp, new Rect(0, top, pw, top + ph), m, PdfRenderMode.ForDisplay)
                            top <- top + ph + gapPx
                        let tmp = output + ".tmp"
                        do
                            use fs = File.Create tmp
                            bmp.Compress(Bitmap.CompressFormat.Png, 100, fs) |> ignore
                        File.Move(tmp, output, true)
                    doneCount <- doneCount + 1
                    progress doneCount

// ---------------------------------------------------------------------------------------------
// Platform
// ---------------------------------------------------------------------------------------------

/// PDFs opened with or shared to the app, delivered once the app is ready for them.
module Incoming =
    let private queue = ConcurrentQueue<string * string>()
    let mutable private handler: (string * string -> unit) option = None

    let private flush () =
        match handler with
        | Some h ->
            let mutable item = Unchecked.defaultof<_>
            while queue.TryDequeue(&item) do
                let captured = item
                Avalonia.Threading.Dispatcher.UIThread.Post(fun () -> h captured)
        | None -> ()

    let deliver (path: string, name: string) =
        queue.Enqueue((path, name))
        flush ()

    let setHandler (h: string * string -> unit) =
        handler <- Some h
        flush ()

type AndroidPlatform(context: Context) =
    let player = AndroidPlayer()
    let speech = lazy (SystemSpeech(context) :> Synth.ISpeechEngine)

    /// The visible activity, for window flags.
    static member val Activity: Android.App.Activity = null with get, set

    interface IPlatform with
        member _.DataDir = context.FilesDir.AbsolutePath

        member _.RenderCrops(pdf, crops, progress, ct) =
            Task.Run((fun () -> Crops.render pdf crops progress ct), ct)

        member _.SystemSpeech = Some speech.Value
        member _.Player = player :> IAudioPlayer

        member _.KeepScreenOn(on) =
            match AndroidPlatform.Activity with
            | null -> ()
            | a ->
                a.RunOnUiThread(fun () ->
                    if on then a.Window.AddFlags WindowManagerFlags.KeepScreenOn
                    else a.Window.ClearFlags WindowManagerFlags.KeepScreenOn)

        member _.SetIncomingPdfHandler(h) = Incoming.setHandler h
