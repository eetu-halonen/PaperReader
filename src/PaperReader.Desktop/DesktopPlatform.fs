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
// Cropping the math out of the PDF (pdftoppm renders, SkiaSharp stacks)
// ---------------------------------------------------------------------------------------------

module Crops =
    let private gapPx = 24

    let private toArgb (bmp: SKBitmap) =
        use bgra = bmp.Copy(SKColorType.Bgra8888)
        let bytes = bgra.Bytes
        Array.init (bgra.Width * bgra.Height) (fun i ->
            let b, g, r, a = int bytes.[4 * i], int bytes.[4 * i + 1], int bytes.[4 * i + 2], int bytes.[4 * i + 3]
            (a <<< 24) ||| (r <<< 16) ||| (g <<< 8) ||| b)

    let private fromArgb (px: int[]) (w: int) (h: int) =
        let bytes = Array.zeroCreate<byte> (w * h * 4)
        for i in 0 .. px.Length - 1 do
            let c = px.[i]
            bytes.[4 * i] <- byte c
            bytes.[4 * i + 1] <- byte (c >>> 8)
            bytes.[4 * i + 2] <- byte (c >>> 16)
            bytes.[4 * i + 3] <- byte (c >>> 24)
        let bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul)
        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length)
        bmp

    /// Same output as the Android renderer: about 3x the paper's size, parts tidied, centred and stacked.
    let render (pdfPath: string) (crops: (Visual * string) list) (progress: int -> unit) (ct: CancellationToken) : Task =
        task {
            let pdftoppm = Tools.require "pdftoppm" "poppler-utils"
            let temp = Path.Combine(Path.GetTempPath(), "paperreader-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory temp |> ignore
            try
                let mutable doneCount = 0
                for (v, output) in crops do
                    ct.ThrowIfCancellationRequested()
                    if not (File.Exists output) then
                        let widest = v.Parts |> Array.map (fun r -> r.W) |> Array.max
                        let scale = min 3.0 (2400.0 / max 1.0 widest)
                        let dropNumber = v.Kind = VisualKind.Equation && v.EqNumber.IsSome
                        let parts = ResizeArray<SKBitmap>()
                        try
                            for k in 0 .. v.Parts.Length - 1 do
                                let r = v.Parts.[k]
                                let stem = Path.Combine(temp, sprintf "%s_%d" v.Id k)
                                do! Tools.run pdftoppm
                                        [ "-png"; "-r"; string (int (72.0 * scale)); "-f"; string (r.Page + 1); "-l"; string (r.Page + 1)
                                          "-x"; string (int (r.X * scale)); "-y"; string (int (r.Y * scale))
                                          "-W"; string (max 1 (int (r.W * scale))); "-H"; string (max 1 (int (r.H * scale)))
                                          "-singlefile"; pdfPath; stem ] None ct
                                use raw = SKBitmap.Decode(stem + ".png")
                                let px, w, h = CropTidy.tidy (toArgb raw) raw.Width raw.Height dropNumber
                                parts.Add(fromArgb px w h)
                            let w = parts |> Seq.map (fun b -> b.Width) |> Seq.max
                            let h = (parts |> Seq.sumBy (fun b -> b.Height)) + gapPx * (parts.Count - 1)
                            use bmp = new SKBitmap(w, h)
                            use canvas = new SKCanvas(bmp)
                            canvas.Clear SKColors.White
                            use divider = new SKPaint(Color = SKColor(224uy, 226uy, 230uy), StrokeWidth = 3.0f)
                            let mutable top = 0
                            for k in 0 .. parts.Count - 1 do
                                if k > 0 then
                                    let y = float32 (top - gapPx / 2)
                                    canvas.DrawLine(0.0f, y, float32 w, y, divider)
                                canvas.DrawBitmap(parts.[k], float32 ((w - parts.[k].Width) / 2), float32 top)
                                top <- top + parts.[k].Height + gapPx
                            let tmp = output + ".tmp"
                            do
                                use image = SKImage.FromBitmap bmp
                                use data = image.Encode(SKEncodedImageFormat.Png, 100)
                                use fs = File.Create tmp
                                data.SaveTo fs
                            File.Move(tmp, output, true)
                        finally
                            for b in parts do b.Dispose()
                    doneCount <- doneCount + 1
                    progress doneCount
            finally
                try Directory.Delete(temp, true) with _ -> ()
        }

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
        member _.RenderCrops(pdf, crops, progress, ct) = Crops.render pdf crops progress ct
        member _.SystemSpeech = speech
        member _.Player = player :> IAudioPlayer
        member _.KeepScreenOn(_) = ()
        member _.SetPlayback(_, _, _) = ()
        member _.EndPlayback() = ()
        member _.SetRemoteHandler(_) = ()

        member _.SetIncomingPdfHandler(h) =
            handler <- Some h
            flush ()
