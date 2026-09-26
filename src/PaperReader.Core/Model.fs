namespace PaperReader.Core

/// A rectangle on a PDF page in points, origin at the top-left of the page's crop box.
type PageRect =
    { Page: int
      X: float
      Y: float
      W: float
      H: float }

[<RequireQualifiedAccess>]
type VisualKind =
    /// A display equation cropped from the page.
    | Equation
    /// The lines of a sentence that contain inline math.
    | Inline
    /// A pseudo-code listing ("Algorithm 1: ...").
    | Algorithm
    /// A figure (chart, diagram, photo) with its caption, found by Mistral OCR.
    | Figure
    /// A table with its caption, found by Mistral OCR.
    | Table

/// An image cut out of the PDF and shown while the related narration plays.
type Visual =
    { Id: string
      Kind: VisualKind
      /// Regions of one page, shown stacked top to bottom (inline math is cut into its fragments).
      Parts: PageRect[]
      /// Number as printed in the paper (equation, algorithm, figure or table), e.g. "3" or "A.2".
      EqNumber: string option
      /// Text extracted from the region (garbled for math, but useful context for the narrator).
      RawText: string
      /// The formula as LaTeX, read by Mistral OCR (when a key is set).
      Latex: string option }

    member v.Page = v.Parts.[0].Page

[<RequireQualifiedAccess>]
type UnitKind =
    | Title
    | Heading
    | Sentence
    | Equation

/// One unit of the paper in reading order, as found by the layout analysis.
type SourceUnit =
    { Id: string
      Kind: UnitKind
      /// Extracted text with sub/superscripts marked LaTeX-style (x_{i}, x^{2}); fed to the narrator model.
      Text: string
      /// Offline narration: the text rewritten for a speech engine without any model.
      Spoken: string
      Visual: string option
      Page: int
      Section: int
      ParagraphEnd: bool }

type Section = { Title: string; FirstSegment: int }

[<RequireQualifiedAccess>]
type ShowReason =
    /// The segment narrates this visual itself.
    | Own
    /// The segment talks about an equation shown earlier.
    | Reference
    /// The equation was just discussed and stays up while its explanation continues.
    | Recent

/// One spoken clip. Audio for each segment is synthesized and cached separately.
type Segment =
    { Index: int
      Kind: UnitKind
      Say: string
      Show: string option
      Reason: ShowReason
      Section: int
      Page: int
      PauseAfterMs: int }

/// The narration script for one paper. Reference equality keeps Elmish model diffs cheap.
[<ReferenceEquality>]
type Script =
    { Version: int
      Title: string
      PageCount: int
      Narrator: string
      Sections: Section[]
      Segments: Segment[]
      Visuals: Visual[] }

    member this.Visual(id: string) =
        this.Visuals |> Array.tryFind (fun v -> v.Id = id)

module Script =
    let currentVersion = 1

/// What the layout analysis produced for a PDF.
type Analysis =
    { Title: string
      PageCount: int
      Sections: string[]
      Units: SourceUnit[]
      Visuals: Visual[] }

type PaperInfo =
    { Id: string
      Title: string
      PageCount: int
      AddedUtc: System.DateTime
      SegmentCount: int
      /// Last listened segment, for resuming and progress display.
      LastSegment: int }

/// One question the listener asked about a paper, and the answer.
type HelpTurn =
    { Question: string
      Answer: string
      /// Segment the listener was at when asking.
      Segment: int
      /// The equation or figure the question was about, if any.
      About: string option
      /// An equation or figure the answer points to, shown with it.
      Show: string option
      /// Follow-up questions offered as one-tap chips.
      Followups: string list
      AskedUtc: System.DateTime }

type Settings =
    { MistralApiKey: string
      /// Narrate with a Mistral chat model (explains equations, fixes extraction errors).
      UseMistralNarration: bool
      NarrationModel: string
      /// Speak with Mistral Voxtral; otherwise the phone's own text-to-speech.
      UseMistralVoice: bool
      VoiceId: string
      VoiceName: string
      Speed: float
      /// Pause after an equation has been read and explained, until the listener continues.
      StopAtEquations: bool
      /// The same for figures and tables, after they are first shown and discussed.
      StopAtFigures: bool
      /// Model that answers the listener's questions (Ask).
      HelpModel: string
      /// A line about the listener ("biology PhD student, rusty on linear algebra"), so answers fit them.
      AboutMe: string
      /// Optional free OpenAlex key, for more than the ~100 searches a day allowed without one (Find papers).
      OpenAlexKey: string }

module Settings =
    let defaults =
        { MistralApiKey = ""
          UseMistralNarration = true
          NarrationModel = "mistral-medium-latest"
          UseMistralVoice = true
          VoiceId = "en_paul_neutral"
          VoiceName = "Paul - Neutral"
          Speed = 1.0
          StopAtEquations = false
          StopAtFigures = false
          HelpModel = "zai-glm-5-3"
          AboutMe = ""
          OpenAlexKey = "" }

    let hasKey (s: Settings) = not (System.String.IsNullOrWhiteSpace s.MistralApiKey)

    /// Folder name for cached audio, so changing the voice never plays stale clips.
    let voiceKey (s: Settings) =
        if hasKey s && s.UseMistralVoice then
            let safe = s.VoiceId |> String.map (fun c -> if System.Char.IsLetterOrDigit c then c else '_')
            "mistral_" + safe
        else
            "system"
