namespace PaperReader

open System.Threading
open System.Threading.Tasks
open PaperReader.Core

/// Plays one cached clip at a time. Callbacks are raised on the UI thread.
type IAudioPlayer =
    abstract Play: path: string * startMs: int * speed: float * onEnded: (unit -> unit) * onError: (string -> unit) -> unit
    abstract Stop: unit -> unit
    abstract PositionMs: int
    abstract SetSpeed: float -> unit

/// What the phone provides to the shared app.
type IPlatform =
    /// Folder for the paper cache and settings.
    abstract DataDir: string
    /// Renders page regions of a PDF to PNG files.
    abstract RenderCrops: pdfPath: string * crops: (Visual * string) list * progress: (int -> unit) * ct: CancellationToken -> Task
    /// The phone's own text-to-speech, used without a Mistral key.
    abstract SystemSpeech: Synth.ISpeechEngine option
    abstract Player: IAudioPlayer
    abstract KeepScreenOn: bool -> unit
    /// PDFs shared to or opened with the app arrive here (a local copy of the file, and its display name).
    abstract SetIncomingPdfHandler: (string * string -> unit) -> unit

/// Platform services set once at startup by the Android head.
module Services =
    let mutable platform: IPlatform option = None

    /// The window root, needed for the file picker.
    let mutable topLevel: Avalonia.Controls.TopLevel option = None

    let get () =
        match platform with
        | Some p -> p
        | None -> failwith "platform not initialised"
