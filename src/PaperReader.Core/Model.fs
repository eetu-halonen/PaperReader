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

/// An image shown while the related narration plays: cut out of a PDF page, or (for other documents)
/// drawn once at import from the picture, formula, table or listing the document holds.
type Visual =
    { Id: string
      Kind: VisualKind
      /// Page (slide, chapter) the visual is on.
      Page: int
      /// Regions of one PDF page, shown stacked top to bottom (inline math is cut into its fragments).
      /// Empty for documents that aren't PDFs: their images are drawn at import.
      Parts: PageRect[]
      /// Number as printed in the paper (equation, algorithm, figure or table), e.g. "3" or "A.2".
      EqNumber: string option
      /// Text extracted from the region (garbled for math, but useful context for the narrator).
      RawText: string
      /// The formula as LaTeX, read by Mistral OCR (when a key is set).
      Latex: string option }

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

/// What the analysis of a document produced: its units in reading order and its visuals.
type Analysis =
    { Title: string
      /// What kind of document this is and how its text was read, for the narrator ("a web page", "slides").
      Source: string
      PageCount: int
      Sections: string[]
      Units: SourceUnit[]
      Visuals: Visual[] }

type PaperInfo =
    { Id: string
      Title: string
      /// What the document was imported from ("pdf", "epub", "html", ...; see Formats).
      Format: string
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

/// Where a card is in learning (as in FSRS and Anki).
[<RequireQualifiedAccess>]
type CardStage =
    /// Never reviewed.
    | New
    /// Being learned: seen again within minutes until it is known.
    | Learning
    /// Known: seen again after days, when it is about to be forgotten.
    | Review
    /// Forgotten in a review: relearned within minutes, then back to days.
    | Relearning

/// A card's memory state, updated by every review (see Fsrs).
type Memory =
    { Stage: CardStage
      Due: System.DateTime
      /// Days until recall drops to 90%; 0 for a new card.
      Stability: float
      /// 1 (easy) to 10 (hard); 0 for a new card.
      Difficulty: float
      Reps: int
      Lapses: int
      LastReview: System.DateTime option }

/// A flashcard about a paper. Its text may have inline $LaTeX$ and **bold**.
type Card =
    { Id: string
      Front: string
      Back: string
      /// An equation, figure or table of the paper shown with the card.
      Visual: string option
      /// The visual is part of the question (otherwise it is shown with the answer).
      VisualOnFront: bool
      /// Where in the narration the card's subject is, for listening to it again.
      Segment: int
      /// What was asked for: "paper" (made from the whole paper), or the listener's request.
      Origin: string
      CreatedUtc: System.DateTime
      Memory: Memory }

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
      /// The reader shows the simple player: big buttons and the equation large, for listening on the move.
      WalkingMode: bool
      /// Model that answers the listener's questions (Ask).
      HelpModel: string
      /// A line about the listener ("biology PhD student, rusty on linear algebra"), so answers fit them.
      AboutMe: string
      /// Optional free OpenAlex key, for more than the ~100 searches a day allowed without one (Find papers).
      OpenAlexKey: string
      /// Share of cards to still remember when they come up for review (FSRS's desired retention).
      /// Higher means more reviews.
      Retention: float }

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
          WalkingMode = false
          HelpModel = "zai-glm-5-3"
          AboutMe = ""
          OpenAlexKey = ""
          Retention = 0.9 }

    let hasKey (s: Settings) = not (System.String.IsNullOrWhiteSpace s.MistralApiKey)

    /// Folder name for cached audio, so changing the voice never plays stale clips.
    let voiceKey (s: Settings) =
        if hasKey s && s.UseMistralVoice then
            let safe = s.VoiceId |> String.map (fun c -> if System.Char.IsLetterOrDigit c then c else '_')
            "mistral_" + safe
        else
            "system"
