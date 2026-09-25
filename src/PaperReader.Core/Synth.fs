/// Background speech synthesis that keeps the audio cache filled just ahead of the listener.
module PaperReader.Core.Synth

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

/// A text-to-speech engine producing WAV bytes.
type ISpeechEngine =
    abstract Key: string
    abstract Parallelism: int
    abstract Synthesize: text: string * ct: CancellationToken -> Task<byte[]>

type MistralEngine(apiKey: string, voiceId: string) =
    interface ISpeechEngine with
        member _.Key = Settings.voiceKey { Settings.defaults with MistralApiKey = apiKey; UseMistralVoice = true; VoiceId = voiceId }
        member _.Parallelism = 3
        member _.Synthesize(text, ct) = Mistral.speech apiKey voiceId text ct

/// Durations (ms) of clips already in the cache for this paper and voice.
let cachedDurations (audioPath: int -> string) (count: int) : Map<int, int> =
    [ for i in 0 .. count - 1 do
          let f = audioPath i
          if File.Exists f then
              let d = try Wav.durationMs f with _ -> 0
              if d > 0 then yield i, d ]
    |> Map.ofList

/// Synthesizes clips from the cursor forward (up to `lookahead` clips ahead) and caches them.
/// Callbacks arrive on worker threads.
type Synthesizer
    (
        script: Script,
        engine: ISpeechEngine,
        audioPath: int -> string,
        lookahead: int,
        onReady: int -> int -> unit,
        onError: int -> string -> unit
    ) =
    let cts = new CancellationTokenSource()
    let signal = new SemaphoreSlim(0, Int32.MaxValue)
    let gate = obj ()
    let inFlight = HashSet<int>()
    let failedUntil = Dictionary<int, DateTime>()
    let mutable cursor = 0

    let pick () =
        lock gate (fun () ->
            let last = min (script.Segments.Length - 1) (cursor + lookahead)
            let now = DateTime.UtcNow
            seq { cursor .. last }
            |> Seq.tryFind (fun i ->
                not (inFlight.Contains i)
                && not (File.Exists(audioPath i))
                && (match failedUntil.TryGetValue i with
                    | true, t -> t < now
                    | _ -> true))
            |> Option.map (fun i ->
                inFlight.Add i |> ignore
                i))

    let worker () =
        task {
            while not cts.IsCancellationRequested do
                match pick () with
                | Some i ->
                    try
                        try
                            let seg = script.Segments.[i]
                            let! wav = engine.Synthesize(seg.Say, cts.Token)
                            let path = audioPath i
                            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                            let duration = Wav.normalizeTo wav seg.PauseAfterMs path
                            onReady i duration
                        with
                        | :? OperationCanceledException when cts.IsCancellationRequested -> ()
                        | e ->
                            let msg =
                                match e with
                                | Mistral.MistralError(_, m) -> m
                                | e when (e :? AggregateException) -> e.InnerException.Message
                                | e -> e.Message
                            lock gate (fun () -> failedUntil.[i] <- DateTime.UtcNow.AddSeconds 12.0)
                            onError i msg
                    finally
                        lock gate (fun () -> inFlight.Remove i |> ignore)
                | None ->
                    try
                        let! _ = signal.WaitAsync(TimeSpan.FromSeconds 3.0, cts.Token)
                        ()
                    with :? OperationCanceledException -> ()
        }

    member _.Start() =
        for _ in 1 .. max 1 engine.Parallelism do
            Task.Run(fun () -> worker () :> Task) |> ignore

    /// Moves the synthesis window to start at segment `i` (the one being listened to).
    member _.SetCursor(i: int) =
        lock gate (fun () ->
            cursor <- max 0 (min i (script.Segments.Length - 1))
            failedUntil.Remove cursor |> ignore)
        signal.Release(max 1 engine.Parallelism) |> ignore

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            signal.Release() |> ignore
