namespace PaperReader.Desktop

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Avalonia.Threading
open SkiaSharp
open PaperReader
open PaperReader.Core

module private Tools =
    /// Full path of a program on PATH.
    let find (name: string) =
        (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "").Split(':')
        |> Array.map (fun d -> Path.Combine(d, name))
        |> Array.tryFind File.Exists

    let require (name: string) (package: string) =
        match find name with
        | Some p -> p
        | None -> failwithf "'%s' is not installed. Install it with: sudo apt install %s" name package

    /// Runs a program to completion; fails with its error output.
    let run (exe: string) (args: string list) (stdin: string option) (ct: CancellationToken) =
        task {
            let psi = ProcessStartInfo(exe, UseShellExecute = false, RedirectStandardError = true, RedirectStandardInput = stdin.IsSome)
            for a in args do psi.ArgumentList.Add a
            use p = Process.Start psi
            match stdin with
            | Some text ->
                do! p.StandardInput.WriteAsync text
                p.StandardInput.Close()
            | None -> ()
            let! err = p.StandardError.ReadToEndAsync(ct)
            do! p.WaitForExitAsync(ct)
            if p.ExitCode <> 0 then failwithf "%s failed: %s" (Path.GetFileName exe) (err.Trim())
        }

// ---------------------------------------------------------------------------------------------
// Audio playback
// ---------------------------------------------------------------------------------------------

/// Plays one WAV clip at a time with ffplay; speed changes use the atempo filter (pitch kept).
type FfplayPlayer() =
    let gate = obj ()
    let mutable current: Process = null
    let mutable clip = ""
    let mutable baseMs = 0
    let mutable speed = 1.0
    let clock = Stopwatch()
    let mutable onEnded: unit -> unit = ignore
    let mutable onError: string -> unit = ignore

    let kill () =
        match current with
        | null -> ()
        | p ->
            current <- null
            try
                if not p.HasExited then p.Kill()
            with _ -> ()
            p.Dispose()

    let position () =
        if isNull current then baseMs else baseMs + int (float clock.ElapsedMilliseconds * speed)

    let launch (startMs: int) =
        kill ()
        let exe = Tools.require "ffplay" "ffmpeg"
        let psi = ProcessStartInfo(exe, UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true)
        for a in [ "-nodisp"; "-autoexit"; "-loglevel"; "error"; "-ss"; sprintf "%.3f" (float startMs / 1000.0)
                   "-af"; sprintf "atempo=%.3f" speed; clip ] do
            psi.ArgumentList.Add a
        let p = new Process(StartInfo = psi, EnableRaisingEvents = true)
        let errors = Text.StringBuilder()
        p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then lock errors (fun () -> errors.AppendLine e.Data |> ignore))
        p.Exited.Add(fun _ ->
            lock gate (fun () ->
                if obj.ReferenceEquals(current, p) then
                    current <- null
                    baseMs <- position ()
                    // ffplay exits with 0 even when it can't read the file: judge by its error output
                    let message = lock errors (fun () -> errors.ToString().Trim())
                    let ok = p.ExitCode = 0 && message = ""
                    let ended, failed = onEnded, onError
                    Dispatcher.UIThread.Post(fun () -> if ok then ended () else failed (if message = "" then "ffplay failed" else message))))
        p.Start() |> ignore
        p.BeginErrorReadLine()
        baseMs <- startMs
        clock.Restart()
        current <- p

    member _.Shutdown() = lock gate kill

    interface IAudioPlayer with
        member _.Play(path, startMs, playbackSpeed, ended, failed) =
            lock gate (fun () ->
                clip <- path
                speed <- playbackSpeed
                onEnded <- ended
                onError <- failed
                try launch startMs
                with e ->
                    kill ()
                    Dispatcher.UIThread.Post(fun () -> failed e.Message))

        member _.Stop() = lock gate kill

        member _.PositionMs = lock gate position

        member _.SetSpeed(s) =
            lock gate (fun () ->
                if isNull current then speed <- s
                else
                    // ffplay can't change tempo while playing: restart from the same place
                    let pos = position ()
                    speed <- s
                    try launch pos with _ -> ())

// ---------------------------------------------------------------------------------------------
// Offline text-to-speech (espeak-ng, when installed)
// ---------------------------------------------------------------------------------------------

type EspeakEngine(exe: string, tempDir: string) =
    interface Synth.ISpeechEngine with
        member _.Key = "system"
        member _.Parallelism = 2

        member _.Synthesize(text, ct) =
            task {
                Directory.CreateDirectory tempDir |> ignore
                let file = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".wav")
                try
                    do! Tools.run exe [ "-v"; "en-us"; "-s"; "165"; "--stdin"; "-w"; file ] (Some text) ct
                    return File.ReadAllBytes file
                finally
                    try File.Delete file with _ -> ()
            }

// ---------------------------------------------------------------------------------------------
// Rendering PDF regions (the equation images are assembled in the shared Crops module)
// ---------------------------------------------------------------------------------------------

/// Renders regions of one PDF: pdftoppm draws each whole page once, regions are cut from memory.
type DesktopPdf(path: string) =
    let pdftoppm = Tools.require "pdftoppm" "poppler-utils"
    let temp = Path.Combine(Path.GetTempPath(), "paperreader-" + Guid.NewGuid().ToString("N"))
    // the last two rendered pages: (page, dpi) -> ARGB pixels
    let cache = Collections.Generic.List<(int * int) * (int[] * int * int)>()

    let renderPage (page: int) (dpi: int) =
        match cache |> Seq.tryFind (fun (k, _) -> k = (page, dpi)) with
        | Some (_, px) -> px
        | None ->
            Directory.CreateDirectory temp |> ignore
            let stem = Path.Combine(temp, "page")
            (Tools.run pdftoppm
                [ "-png"; "-r"; string dpi; "-f"; string (page + 1); "-l"; string (page + 1); "-singlefile"; path; stem ]
                None CancellationToken.None).GetAwaiter().GetResult()
            use bmp = SKBitmap.Decode(stem + ".png")
            use bgra = bmp.Copy(SKColorType.Bgra8888)
            let bytes = bgra.Bytes
            let px =
                Array.init (bgra.Width * bgra.Height) (fun i ->
                    (int bytes.[4 * i + 3] <<< 24) ||| (int bytes.[4 * i + 2] <<< 16) ||| (int bytes.[4 * i + 1] <<< 8) ||| int bytes.[4 * i])
            let result = px, bgra.Width, bgra.Height
            cache.Add(((page, dpi), result))
            if cache.Count > 2 then cache.RemoveAt 0
            result

    interface IPdfPages with
        member _.Render(page, r, scale) =
            let dpi = int (round (72.0 * scale))
            let px, pw, ph = renderPage page dpi
            let s = float dpi / 72.0
            let x0, y0 = int (round (r.X * s)), int (round (r.Y * s))
            let w, h = max 1 (int (round (r.W * s))), max 1 (int (round (r.H * s)))
            let white = 0xFFFFFFFF |> int
            let out = Array.create (w * h) white
            for y in 0 .. h - 1 do
                let sy = y0 + y
                if sy >= 0 && sy < ph then
                    for x in 0 .. w - 1 do
                        let sx = x0 + x
                        if sx >= 0 && sx < pw then out.[y * w + x] <- px.[sy * pw + sx]
            out, w, h

    interface IDisposable with
        member _.Dispose() =
            cache.Clear()
            // the folder only ever holds page.png: remove that file, then the (now empty) folder
            try
                File.Delete(Path.Combine(temp, "page.png"))
                if Directory.Exists temp then Directory.Delete temp
            with _ -> ()

// ---------------------------------------------------------------------------------------------
// Platform
// ---------------------------------------------------------------------------------------------

type DesktopPlatform() =
    let dataDir =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "PaperReader")
    let player = FfplayPlayer()
    let speech =
        Tools.find "espeak-ng"
        |> Option.map (fun exe -> EspeakEngine(exe, Path.Combine(Path.GetTempPath(), "paperreader-tts")) :> Synth.ISpeechEngine)
    let pending = ConcurrentQueue<string * string>()
    let mutable handler: (string * string -> unit) option = None

    let flush () =
        match handler with
        | Some h ->
            let mutable item = Unchecked.defaultof<_>
            while pending.TryDequeue(&item) do
                let captured = item
                Dispatcher.UIThread.Post(fun () -> h captured)
        | None -> ()

    do Directory.CreateDirectory dataDir |> ignore

    /// A PDF given on the command line.
    member _.Open(path: string) =
        pending.Enqueue((path, Path.GetFileName path))
        flush ()

    member _.Shutdown() = player.Shutdown()

    interface IPlatform with
        member _.DataDir = dataDir
        member _.OpenPdf(pdf) = new DesktopPdf(pdf) :> IPdfPages
        member _.SystemSpeech = speech
        member _.Player = player :> IAudioPlayer
        member _.KeepScreenOn(_) = ()
        member _.SetPlayback(_, _, _) = ()
        member _.EndPlayback() = ()
        member _.SetRemoteHandler(_) = ()

        member _.SetIncomingPdfHandler(h) =
            handler <- Some h
            flush ()
