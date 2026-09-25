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

/// An open PDF whose pages can be rendered. Used from one thread at a time.
type IPdfPages =
    inherit System.IDisposable
    /// A page region at `scale` pixels per point, as ARGB pixels on white: (pixels, width, height).
    abstract Render: page: int * region: PageRect * scale: float -> int[] * int * int

/// Records a spoken question from the microphone.
type IRecorder =
    /// Starts recording (asking for microphone permission first where needed).
    abstract Start: unit -> Task<unit>
    /// Stops and returns the audio with a file name whose extension tells its format (wav, m4a, webm).
    abstract Stop: unit -> Task<byte[] * string>
    /// Stops and throws the recording away.
    abstract Cancel: unit -> unit

/// What the device provides to the shared app.
type IPlatform =
    /// Folder for the paper cache and settings.
    abstract DataDir: string
    /// Opens a PDF for rendering page regions (the equation images).
    abstract OpenPdf: pdfPath: string -> IPdfPages
    /// The phone's own text-to-speech, used without a Mistral key.
    abstract SystemSpeech: Synth.ISpeechEngine option
    abstract Player: IAudioPlayer
    /// The microphone, for asking questions by voice (None where recording isn't available).
    abstract Recorder: IRecorder option
    abstract KeepScreenOn: bool -> unit
    /// Background playback and the media notification: paper title, current section, playing or paused.
    abstract SetPlayback: title: string * detail: string * playing: bool -> unit
    /// Removes the media notification (the reader was closed).
    abstract EndPlayback: unit -> unit
    /// Play (true) or pause (false) requests from outside the app: notification, headset, calls.
    abstract SetRemoteHandler: (bool -> unit) -> unit
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
