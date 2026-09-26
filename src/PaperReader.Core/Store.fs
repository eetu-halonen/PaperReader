/// On-disk cache: one folder per paper (the document, script, images, audio clips per voice).
/// JSON is read and written by hand so nothing depends on reflection (safe under Android trimming).
module PaperReader.Core.Store

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

let private writeAtomic (path: string) (write: Utf8JsonWriter -> unit) =
    let tmp = path + ".tmp"
    do
        use fs = File.Create tmp
        use w = new Utf8JsonWriter(fs, JsonWriterOptions(Indented = false))
        write w
        w.Flush()
    File.Move(tmp, path, true)

let private str (e: JsonElement) (name: string) (fallback: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> fallback

let private num (e: JsonElement) (name: string) (fallback: float) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
    | _ -> fallback

let private boolean (e: JsonElement) (name: string) (fallback: bool) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.True -> true
    | true, v when v.ValueKind = JsonValueKind.False -> false
    | _ -> fallback

let private optStr (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

// ---- layout of the data folder

type Paths(root: string) =
    member _.Root = root
    member _.Papers = Path.Combine(root, "papers")
    member _.Settings = Path.Combine(root, "settings.json")
    member this.Paper(id: string) = Path.Combine(this.Papers, id)
    member this.Pdf(id) = Path.Combine(this.Paper id, "paper.pdf")
    /// The imported document when it isn't a PDF, with its own extension ("document.epub").
    member this.Document(id, extension: string) = Path.Combine(this.Paper id, "document" + extension)
    member this.Script(id) = Path.Combine(this.Paper id, "script.json")
    member this.Meta(id) = Path.Combine(this.Paper id, "meta.json")
    member this.Images(id) = Path.Combine(this.Paper id, "img")
    member this.Image(id, visualId: string) = Path.Combine(this.Images id, visualId + ".png")
    member this.AudioDir(id, voiceKey: string) = Path.Combine(this.Paper id, "audio", voiceKey)
    member this.Audio(id, voiceKey, index: int) = Path.Combine(this.AudioDir(id, voiceKey), sprintf "%05d.wav" index)
    /// The paper's full text as markdown with LaTeX (read by Mistral OCR from a PDF, or written from the
    /// document at import), the source for answering questions.
    member this.PaperText(id) = Path.Combine(this.Paper id, "paper.md")
    member this.HelpDir(id) = Path.Combine(this.Paper id, "help")
    /// Questions asked about the paper and their answers.
    member this.HelpLog(id) = Path.Combine(this.HelpDir id, "turns.json")
    /// Detailed descriptions of the figures (made once with a vision model), since the answering model can't see images.
    member this.FigureNotes(id) = Path.Combine(this.HelpDir id, "figures.json")
    /// The answer being read aloud, a few sentences per clip.
    member this.AnswerAudio(id, part: int) = Path.Combine(this.HelpDir id, sprintf "answer-%d.wav" part)
    /// The paper's flashcards and their review state.
    member this.Cards(id) = Path.Combine(this.Paper id, "cards.json")

let paperId (pdfPath: string) =
    use fs = File.OpenRead pdfPath
    let hash = SHA256.HashData(fs)
    Convert.ToHexString(hash, 0, 8).ToLowerInvariant()

// ---- settings

let saveSettings (p: Paths) (s: Settings) =
    Directory.CreateDirectory p.Root |> ignore
    writeAtomic p.Settings (fun w ->
        w.WriteStartObject()
        w.WriteString("mistralApiKey", s.MistralApiKey)
        w.WriteBoolean("useMistralNarration", s.UseMistralNarration)
        w.WriteString("narrationModel", s.NarrationModel)
        w.WriteBoolean("useMistralVoice", s.UseMistralVoice)
        w.WriteString("voiceId", s.VoiceId)
        w.WriteString("voiceName", s.VoiceName)
        w.WriteNumber("speed", s.Speed)
        w.WriteBoolean("stopAtEquations", s.StopAtEquations)
        w.WriteBoolean("stopAtFigures", s.StopAtFigures)
        w.WriteBoolean("walkingMode", s.WalkingMode)
        w.WriteString("helpModel", s.HelpModel)
        w.WriteString("aboutMe", s.AboutMe)
        w.WriteString("openAlexKey", s.OpenAlexKey)
        w.WriteNumber("retention", s.Retention)
        w.WriteEndObject())

let loadSettings (p: Paths) : Settings =
    try
        use d = JsonDocument.Parse(File.ReadAllText p.Settings)
        let e = d.RootElement
        let def = Settings.defaults
        { MistralApiKey = str e "mistralApiKey" def.MistralApiKey
          UseMistralNarration = boolean e "useMistralNarration" def.UseMistralNarration
          NarrationModel = str e "narrationModel" def.NarrationModel
          UseMistralVoice = boolean e "useMistralVoice" def.UseMistralVoice
          VoiceId = str e "voiceId" def.VoiceId
          VoiceName = str e "voiceName" def.VoiceName
          Speed = num e "speed" def.Speed
          StopAtEquations = boolean e "stopAtEquations" def.StopAtEquations
          StopAtFigures = boolean e "stopAtFigures" def.StopAtFigures
          WalkingMode = boolean e "walkingMode" def.WalkingMode
          HelpModel = str e "helpModel" def.HelpModel
          AboutMe = str e "aboutMe" def.AboutMe
          OpenAlexKey = str e "openAlexKey" def.OpenAlexKey
          Retention = num e "retention" def.Retention |> max 0.7 |> min 0.97 }
    with _ -> Settings.defaults

// ---- paper metadata (library entry + listening position)

let saveMeta (p: Paths) (m: PaperInfo) =
    writeAtomic (p.Meta m.Id) (fun w ->
        w.WriteStartObject()
        w.WriteString("id", m.Id)
        w.WriteString("title", m.Title)
        w.WriteString("format", m.Format)
        w.WriteNumber("pageCount", m.PageCount)
        w.WriteString("addedUtc", m.AddedUtc.ToString("o"))
        w.WriteNumber("segmentCount", m.SegmentCount)
        w.WriteNumber("lastSegment", m.LastSegment)
        w.WriteEndObject())

let loadMeta (p: Paths) (id: string) : PaperInfo option =
    try
        use d = JsonDocument.Parse(File.ReadAllText(p.Meta id))
        let e = d.RootElement
        Some
            { Id = str e "id" id
              Title = str e "title" "Untitled"
              Format = str e "format" "pdf"
              PageCount = int (num e "pageCount" 0.0)
              AddedUtc = (match DateTime.TryParse(str e "addedUtc" "") with | true, t -> t.ToUniversalTime() | _ -> DateTime.UtcNow)
              SegmentCount = int (num e "segmentCount" 0.0)
              LastSegment = int (num e "lastSegment" 0.0) }
    with _ -> None

/// All papers that finished processing, most recently added first.
let library (p: Paths) : PaperInfo list =
    if not (Directory.Exists p.Papers) then []
    else
        Directory.GetDirectories p.Papers
        |> Seq.map Path.GetFileName
        |> Seq.filter (fun id -> File.Exists(p.Script id))
        |> Seq.choose (loadMeta p)
        |> Seq.sortByDescending (fun m -> m.AddedUtc)
        |> List.ofSeq

let deletePaper (p: Paths) (id: string) =
    // only ever a paper folder: its name is the 16-hex-digit hash from paperId
    if id.Length = 16 && id |> Seq.forall Uri.IsHexDigit then
        try Directory.Delete(p.Paper id, true) with _ -> ()

// ---- script

let private kindName =
    function
    | UnitKind.Title -> "title"
    | UnitKind.Heading -> "heading"
    | UnitKind.Sentence -> "sentence"
    | UnitKind.Equation -> "equation"

let private kindOf =
    function
    | "title" -> UnitKind.Title
    | "heading" -> UnitKind.Heading
    | "equation" -> UnitKind.Equation
    | _ -> UnitKind.Sentence

let private reasonName =
    function
    | ShowReason.Own -> "own"
    | ShowReason.Reference -> "ref"
    | ShowReason.Recent -> "recent"

let private reasonOf =
    function
    | "ref" -> ShowReason.Reference
    | "recent" -> ShowReason.Recent
    | _ -> ShowReason.Own

let saveScript (p: Paths) (id: string) (s: Script) =
    writeAtomic (p.Script id) (fun w ->
        w.WriteStartObject()
        w.WriteNumber("version", s.Version)
        w.WriteString("title", s.Title)
        w.WriteNumber("pageCount", s.PageCount)
        w.WriteString("narrator", s.Narrator)
        w.WriteStartArray "sections"
        for x in s.Sections do
            w.WriteStartObject()
            w.WriteString("title", x.Title)
            w.WriteNumber("first", x.FirstSegment)
            w.WriteEndObject()
        w.WriteEndArray()
        w.WriteStartArray "visuals"
        for v in s.Visuals do
            w.WriteStartObject()
            w.WriteString("id", v.Id)
            w.WriteString("kind", (match v.Kind with
                                   | VisualKind.Equation -> "equation"
                                   | VisualKind.Algorithm -> "algorithm"
                                   | VisualKind.Figure -> "figure"
                                   | VisualKind.Table -> "table"
                                   | VisualKind.Inline -> "inline"))
            w.WriteNumber("page", v.Page)
            w.WriteStartArray "parts"
            for r in v.Parts do
                w.WriteStartObject()
                w.WriteNumber("page", r.Page)
                w.WriteNumber("x", r.X)
                w.WriteNumber("y", r.Y)
                w.WriteNumber("w", r.W)
                w.WriteNumber("h", r.H)
                w.WriteEndObject()
            w.WriteEndArray()
            match v.EqNumber with
            | Some n -> w.WriteString("number", n)
            | None -> ()
            w.WriteString("raw", v.RawText)
            match v.Latex with
            | Some l -> w.WriteString("latex", l)
            | None -> ()
            w.WriteEndObject()
        w.WriteEndArray()
        w.WriteStartArray "segments"
        for g in s.Segments do
            w.WriteStartObject()
            w.WriteString("kind", kindName g.Kind)
            w.WriteString("say", g.Say)
            match g.Show with
            | Some v -> w.WriteString("show", v)
            | None -> ()
            w.WriteString("reason", reasonName g.Reason)
            w.WriteNumber("section", g.Section)
            w.WriteNumber("page", g.Page)
            w.WriteNumber("pause", g.PauseAfterMs)
            w.WriteEndObject()
        w.WriteEndArray()
        w.WriteEndObject())

let loadScript (p: Paths) (id: string) : Script option =
    try
        use d = JsonDocument.Parse(File.ReadAllText(p.Script id))
        let e = d.RootElement
        let version = int (num e "version" 0.0)
        if version <> Script.currentVersion then None
        else
            Some
                { Version = version
                  Title = str e "title" ""
                  PageCount = int (num e "pageCount" 0.0)
                  Narrator = str e "narrator" ""
                  Sections =
                    [| for x in e.GetProperty("sections").EnumerateArray() ->
                           { Title = str x "title" ""; FirstSegment = int (num x "first" 0.0) } |]
                  Visuals =
                    [| for v in e.GetProperty("visuals").EnumerateArray() ->
                           let parts =
                               [| for r in v.GetProperty("parts").EnumerateArray() ->
                                      { Page = int (num r "page" 0.0); X = num r "x" 0.0; Y = num r "y" 0.0; W = num r "w" 0.0; H = num r "h" 0.0 } |]
                           { Id = str v "id" ""
                             Kind = (match str v "kind" "" with
                                     | "equation" -> VisualKind.Equation
                                     | "algorithm" -> VisualKind.Algorithm
                                     | "figure" -> VisualKind.Figure
                                     | "table" -> VisualKind.Table
                                     | _ -> VisualKind.Inline)
                             // scripts from before visuals had their own page: the page of the first region
                             Page = int (num v "page" (if parts.Length > 0 then float parts.[0].Page else 0.0))
                             Parts = parts
                             EqNumber = optStr v "number"
                             RawText = str v "raw" ""
                             Latex = optStr v "latex" } |]
                  Segments =
                    e.GetProperty("segments").EnumerateArray()
                    |> Seq.mapi (fun i g ->
                        { Index = i
                          Kind = kindOf (str g "kind" "")
                          Say = str g "say" ""
                          Show = optStr g "show"
                          Reason = reasonOf (str g "reason" "")
                          Section = int (num g "section" 0.0)
                          Page = int (num g "page" 0.0)
                          PauseAfterMs = int (num g "pause" 0.0) })
                    |> Array.ofSeq }
    with _ -> None

// ---- questions and answers

let saveHelp (p: Paths) (id: string) (turns: HelpTurn list) =
    Directory.CreateDirectory(p.HelpDir id) |> ignore
    writeAtomic (p.HelpLog id) (fun w ->
        w.WriteStartArray()
        for t in turns do
            w.WriteStartObject()
            w.WriteString("q", t.Question)
            w.WriteString("a", t.Answer)
            w.WriteNumber("segment", t.Segment)
            t.About |> Option.iter (fun v -> w.WriteString("about", v))
            t.Show |> Option.iter (fun v -> w.WriteString("show", v))
            w.WriteStartArray "next"
            for f in t.Followups do w.WriteStringValue f
            w.WriteEndArray()
            w.WriteString("utc", t.AskedUtc.ToString("o"))
            w.WriteEndObject()
        w.WriteEndArray())

let loadHelp (p: Paths) (id: string) : HelpTurn list =
    try
        use d = JsonDocument.Parse(File.ReadAllText(p.HelpLog id))
        [ for e in d.RootElement.EnumerateArray() ->
              { Question = str e "q" ""
                Answer = str e "a" ""
                Segment = int (num e "segment" 0.0)
                About = optStr e "about"
                Show = optStr e "show"
                Followups =
                    match e.TryGetProperty "next" with
                    | true, a when a.ValueKind = JsonValueKind.Array -> [ for f in a.EnumerateArray() -> f.GetString() ]
                    | _ -> []
                AskedUtc = (match DateTime.TryParse(str e "utc" "") with | true, t -> t.ToUniversalTime() | _ -> DateTime.UtcNow) } ]
    with _ -> []

let saveFigureNotes (p: Paths) (id: string) (notes: Map<string, string>) =
    Directory.CreateDirectory(p.HelpDir id) |> ignore
    writeAtomic (p.FigureNotes id) (fun w ->
        w.WriteStartObject()
        for KeyValue (k, v) in notes do w.WriteString(k, v)
        w.WriteEndObject())

let loadFigureNotes (p: Paths) (id: string) : Map<string, string> option =
    try
        use d = JsonDocument.Parse(File.ReadAllText(p.FigureNotes id))
        Some(d.RootElement.EnumerateObject() |> Seq.map (fun e -> e.Name, e.Value.GetString()) |> Map.ofSeq)
    with _ -> None

// ---- flashcards

let private stageName =
    function
    | CardStage.New -> "new"
    | CardStage.Learning -> "learning"
    | CardStage.Review -> "review"
    | CardStage.Relearning -> "relearning"

let private stageOf =
    function
    | "learning" -> CardStage.Learning
    | "review" -> CardStage.Review
    | "relearning" -> CardStage.Relearning
    | _ -> CardStage.New

let private time (e: JsonElement) (name: string) =
    match DateTime.TryParse(str e name "", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
    | true, t -> Some(t.ToUniversalTime())
    | _ -> None

let saveCards (p: Paths) (id: string) (cards: Card list) =
    writeAtomic (p.Cards id) (fun w ->
        w.WriteStartArray()
        for c in cards do
            w.WriteStartObject()
            w.WriteString("id", c.Id)
            w.WriteString("front", c.Front)
            w.WriteString("back", c.Back)
            c.Visual |> Option.iter (fun v -> w.WriteString("visual", v))
            w.WriteBoolean("visualOnFront", c.VisualOnFront)
            w.WriteNumber("segment", c.Segment)
            w.WriteString("origin", c.Origin)
            w.WriteString("created", c.CreatedUtc.ToString("o"))
            let m = c.Memory
            w.WriteString("stage", stageName m.Stage)
            w.WriteString("due", m.Due.ToString("o"))
            w.WriteNumber("stability", m.Stability)
            w.WriteNumber("difficulty", m.Difficulty)
            w.WriteNumber("reps", m.Reps)
            w.WriteNumber("lapses", m.Lapses)
            m.LastReview |> Option.iter (fun t -> w.WriteString("last", t.ToString("o")))
            w.WriteEndObject()
        w.WriteEndArray())

let loadCards (p: Paths) (id: string) : Card list =
    try
        use d = JsonDocument.Parse(File.ReadAllText(p.Cards id))
        [ for e in d.RootElement.EnumerateArray() ->
              let created = time e "created" |> Option.defaultValue DateTime.UtcNow
              { Id = str e "id" (Guid.NewGuid().ToString("N"))
                Front = str e "front" ""
                Back = str e "back" ""
                Visual = optStr e "visual"
                VisualOnFront = boolean e "visualOnFront" false
                Segment = int (num e "segment" 0.0)
                Origin = str e "origin" ""
                CreatedUtc = created
                Memory =
                  { Stage = stageOf (str e "stage" "")
                    Due = time e "due" |> Option.defaultValue created
                    Stability = num e "stability" 0.0
                    Difficulty = num e "difficulty" 0.0
                    Reps = int (num e "reps" 0.0)
                    Lapses = int (num e "lapses" 0.0)
                    LastReview = time e "last" } } ]
    with _ -> []
