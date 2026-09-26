namespace PaperReader.Browser

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Avalonia.Threading
open PaperReader
open PaperReader.Core

module private Bytes =
    /// Copies a JavaScript buffer into .NET memory (and frees it).
    let take (id: int) =
        let bytes = Array.zeroCreate<byte> (Js.Length id)
        Js.Take(id, Span<byte>(bytes))
        bytes

    /// Copies bytes into a JavaScript buffer; returns its id.
    let stage (bytes: byte[]) = Js.Stage(Span<byte>(bytes))

// ---------------------------------------------------------------------------------------------
// Storage
// ---------------------------------------------------------------------------------------------

/// The data folder lives in the WebAssembly file system, which is lost when the page closes, so it is
/// mirrored to IndexedDB: restored at startup, and written back as files change. A paper's audio can be
/// large, so it is restored only when the paper is opened.
type BrowserStorage(root: string) =
    /// Stored paths (relative to root) and the length and write time of what was stored.
    let known = Dictionary<string, int64 * DateTime>()
    let stored = HashSet<string>()
    let restored = HashSet<string>()
    let mutable syncing = false

    let relative (full: string) = Path.GetRelativePath(root, full).Replace('\\', '/')
    let full (rel: string) = Path.Combine(root, rel)
    let isAudio (rel: string) = rel.Contains "/audio/"
    // temporary files and copies of picked PDFs are not worth keeping
    let skip (rel: string) = rel.EndsWith ".tmp" || rel.StartsWith "incoming/"

    let restoreFile (rel: string) =
        task {
            let! id = Js.StoreGet rel
            if id >= 0 then
                let path = full rel
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllBytes(path, Bytes.take id)
                let info = FileInfo path
                known.[rel] <- (info.Length, info.LastWriteTimeUtc)
        }

    member _.Root = root

    /// Brings back everything but audio.
    member _.Start() =
        task {
            Directory.CreateDirectory root |> ignore
            let! list = Js.StoreList()
            for k in list.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
                stored.Add k |> ignore
            for k in List.ofSeq stored do
                if not (isAudio k) then do! restoreFile k
        }

    /// Brings back one folder (a paper's audio for a voice) the first time it is needed.
    member _.Restore(folder: string) : Task =
        task {
            let prefix = (relative folder).TrimEnd('/') + "/"
            if restored.Add prefix then
                for k in stored |> Seq.filter (fun k -> k.StartsWith prefix) |> List.ofSeq do
                    if not (File.Exists(full k)) then do! restoreFile k
        }

    /// Writes new and changed files to IndexedDB and forgets deleted ones.
    member _.Sync() : Task =
        task {
            if not syncing then
                syncing <- true
                try
                    let present = HashSet<string>()
                    for path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
                        let rel = relative path
                        if not (skip rel) then
                            present.Add rel |> ignore
                            let info = FileInfo path
                            let stamp = info.Length, info.LastWriteTimeUtc
                            match known.TryGetValue rel with
                            | true, s when s = stamp -> ()
                            | _ ->
                                do! Js.StorePut(rel, Bytes.stage (File.ReadAllBytes path))
                                known.[rel] <- stamp
                                stored.Add rel |> ignore
                    let gone = known.Keys |> Seq.filter (fun k -> not (present.Contains k)) |> List.ofSeq
                    for k in gone do
                        known.Remove k |> ignore
                        stored.Remove k |> ignore
                        do! Js.StoreDelete k
                    // a removed paper takes its stored audio (never restored here) with it
                    let removedPapers =
                        gone
                        |> List.choose (fun k ->
                            match k.Split('/') with
                            | [| "papers"; id; _ |] | [| "papers"; id; _; _ |] | [| "papers"; id; _; _; _ |] when not (Directory.Exists(full ("papers/" + id))) ->
                                Some("papers/" + id + "/")
                            | _ -> None)
                        |> List.distinct
                    for prefix in removedPapers do
                        do! Js.StoreDeletePrefix prefix
                        stored.RemoveWhere(fun k -> k.StartsWith prefix) |> ignore
                finally
                    syncing <- false
        }

// ---------------------------------------------------------------------------------------------
// Audio, PDF pages, microphone
// ---------------------------------------------------------------------------------------------

/// Plays WAV clips with an HTML audio element (pitch kept when the speed changes).
type BrowserPlayer() =
    interface IAudioPlayer with
        member _.Play(path, startMs, speed, ended, failed) =
            try
                Js.AudioPlay(
                    Bytes.stage (File.ReadAllBytes path), float startMs / 1000.0, speed,
                    Action(fun () -> Dispatcher.UIThread.Post(fun () -> ended ())),
                    Action<string>(fun m -> Dispatcher.UIThread.Post(fun () -> failed m)))
            with e -> Dispatcher.UIThread.Post(fun () -> failed e.Message)

        member _.Stop() = Js.AudioStop()
        member _.PositionMs = int (Js.AudioPosition() * 1000.0)
        member _.SetSpeed(speed) = Js.AudioRate speed

/// PDF pages drawn by pdf.js. The document is opened on first use.
type BrowserPdf(path: string) =
    let doc = lazy (Js.PdfOpen(Bytes.stage (File.ReadAllBytes path)))

    interface IPdfPages with
        member _.Render(page, r, scale) =
            task {
                let! d = doc.Value
                let! id = Js.PdfRender(d, page, r.X, r.Y, r.W, r.H, scale)
                let rgba = Bytes.take id
                let w, h = max 1 (int (floor (r.W * scale))), max 1 (int (floor (r.H * scale)))
                let px = Array.zeroCreate<int> (w * h)
                for i in 0 .. px.Length - 1 do
                    let o = 4 * i
                    px.[i] <- (0xFF <<< 24) ||| (int rgba.[o] <<< 16) ||| (int rgba.[o + 1] <<< 8) ||| int rgba.[o + 2]
                return px, w, h
            }

    interface IDisposable with
        member _.Dispose() =
            if doc.IsValueCreated && doc.Value.IsCompletedSuccessfully then Js.PdfClose doc.Value.Result

/// Records a spoken question with MediaRecorder (the browser asks for the microphone the first time).
type BrowserRecorder() =
    interface IRecorder with
        member _.Start() = task { do! Js.RecStart() }

        member _.Stop() =
            task {
                let! id = Js.RecStop()
                return Bytes.take id, "question." + Js.RecExtension()
            }

        member _.Cancel() = Js.RecCancel()

        member _.Level = Js.RecLevel()

// ---------------------------------------------------------------------------------------------
// Platform
// ---------------------------------------------------------------------------------------------

/// The browser has no speech engine that makes audio files, so reading aloud needs a Mistral key.
type BrowserPlatform(storage: BrowserStorage) =
    let player = BrowserPlayer()
    let recorder = BrowserRecorder() :> IRecorder
    let pending = Queue<string * string>()
    let mutable handler: (string * string -> unit) option = None

    let flush () =
        match handler with
        | Some h ->
            while pending.Count > 0 do
                let item = pending.Dequeue()
                Dispatcher.UIThread.Post(fun () -> h item)
        | None -> ()

    /// A document to open once the app is ready (from the page's ?url= address).
    member _.Open(path: string, name: string) =
        pending.Enqueue((path, name))
        flush ()

    interface IPlatform with
        member _.DataDir = storage.Root
        member _.OpenPdf(pdf) = new BrowserPdf(pdf) :> IPdfPages
        member _.SystemSpeech = None
        member _.Player = player :> IAudioPlayer
        member _.Recorder = Some recorder
        member _.KeepScreenOn(on) = Js.KeepAwake on
        member _.SetPlayback(title, detail, playing) = Js.SetPlayback(title, detail, playing)
        member _.EndPlayback() = Js.EndPlayback()
        member _.SetRemoteHandler(h) = Js.SetRemote(Action<bool>(fun play -> Dispatcher.UIThread.Post(fun () -> h play)))
        member _.SetIncomingFileHandler(h) =
            handler <- Some h
            flush ()
        member _.Restore(folder) = storage.Restore folder
