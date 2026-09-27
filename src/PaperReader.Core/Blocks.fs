/// The common form of every document that isn't read by the PDF layout analysis: a list of blocks (headings,
/// paragraphs, formulas, pictures, tables, listings) that the format readers produce, turned here into the same
/// units and visuals the narrator, Ask and Learn work with.
module PaperReader.Core.Blocks

open System
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks

/// Where a picture's bytes come from.
type ImageSource =
    /// The encoded image (PNG, JPEG, GIF, WebP, BMP), taken out of the document.
    | Data of byte[]
    /// An address to download it from (a web page's picture), fetched at import.
    | Link of string
    /// An area of a PDF page (a figure OCR found), cut out of the PDF when the paper's images are drawn.
    | Region of PageRect

[<RequireQualifiedAccess>]
type Block =
    | Heading of level: int * text: string
    /// Running text. Inline math is written $LaTeX$; sub- and superscripts in text as x_{i}, x^{2}.
    | Paragraph of text: string
    /// A list item: read like a paragraph of its own.
    | Item of text: string
    /// A display formula in LaTeX, with its number as printed ("3", "A.2").
    | Math of latex: string * number: string option
    | Image of source: ImageSource * alt: string * caption: string
    /// A table as rows of cell texts (the first row is usually the header).
    | Table of rows: string list list * caption: string
    /// A code or pseudo-code listing.
    | Code of text: string * caption: string
    /// A new page, slide or chapter starts.
    | Break

type Document =
    { Title: string
      /// What kind of document it is, for the narrator ("a web page", "an EPUB book").
      Source: string
      Blocks: Block list }

/// What a visual's image is drawn from at import (by the app, which can draw).
type Picture =
    /// An encoded image in any common format.
    | Bitmap of byte[]
    /// LaTeX, typeset (one formula per line).
    | Formula of string
    /// A table: rows of cell texts, the first row the header.
    | Grid of string list list
    /// A code listing.
    | Listing of string

// ---------------------------------------------------------------------------------------------
// Pictures
// ---------------------------------------------------------------------------------------------

let private be16 (b: byte[]) i = (int b.[i] <<< 8) ||| int b.[i + 1]
let private le16 (b: byte[]) i = int b.[i] ||| (int b.[i + 1] <<< 8)
let private le32 (b: byte[]) i = int b.[i] ||| (int b.[i + 1] <<< 8) ||| (int b.[i + 2] <<< 16) ||| (int b.[i + 3] <<< 24)
let private be32 (b: byte[]) i = (int b.[i] <<< 24) ||| (int b.[i + 1] <<< 16) ||| (int b.[i + 2] <<< 8) ||| int b.[i + 3]

/// Width and height of a PNG, GIF, JPEG, BMP or WebP from its header, without decoding it. None for other
/// formats (SVG, TIFF, ...), which the app can't draw.
let imageSize (b: byte[]) : (int * int) option =
    try
        if b.Length > 24 && b.[0] = 0x89uy && b.[1] = 0x50uy && b.[2] = 0x4Euy && b.[3] = 0x47uy then Some(be32 b 16, be32 b 20)
        elif b.Length > 10 && b.[0] = 0x47uy && b.[1] = 0x49uy && b.[2] = 0x46uy then Some(le16 b 6, le16 b 8)
        elif b.Length > 26 && b.[0] = 0x42uy && b.[1] = 0x4Duy then Some(abs (le32 b 18), abs (le32 b 22))
        elif b.Length > 30 && Encoding.ASCII.GetString(b, 0, 4) = "RIFF" && Encoding.ASCII.GetString(b, 8, 4) = "WEBP" then
            match Encoding.ASCII.GetString(b, 12, 4) with
            | "VP8 " -> Some(le16 b 26 &&& 0x3FFF, le16 b 28 &&& 0x3FFF)
            | "VP8L" ->
                let bits = int b.[21] ||| (int b.[22] <<< 8) ||| (int b.[23] <<< 16) ||| (int b.[24] <<< 24)
                Some((bits &&& 0x3FFF) + 1, ((bits >>> 14) &&& 0x3FFF) + 1)
            | "VP8X" -> Some((int b.[24] ||| (int b.[25] <<< 8) ||| (int b.[26] <<< 16)) + 1, (int b.[27] ||| (int b.[28] <<< 8) ||| (int b.[29] <<< 16)) + 1)
            | _ -> None
        elif b.Length > 4 && b.[0] = 0xFFuy && b.[1] = 0xD8uy then
            // walk the JPEG segments to the frame header
            let mutable i = 2
            let mutable size = None
            while size.IsNone && i + 9 < b.Length do
                if b.[i] <> 0xFFuy then i <- b.Length
                else
                    let marker = b.[i + 1]
                    if marker = 0xFFuy then i <- i + 1
                    elif marker >= 0xC0uy && marker <= 0xCFuy && marker <> 0xC4uy && marker <> 0xC8uy && marker <> 0xCCuy then
                        size <- Some(be16 b (i + 7), be16 b (i + 5))
                    else i <- i + 2 + be16 b (i + 2)
            size
        else None
    with _ -> None

/// Pictures smaller than this on both sides are icons, bullets or spacers, not figures.
let private minPictureSide = 64

/// True when the image can be drawn and is big enough to be a figure.
let isFigureImage (bytes: byte[]) =
    match imageSize bytes with
    | Some (w, h) -> w > 0 && h > 0 && (w >= minPictureSide || h >= minPictureSide) && w >= 16 && h >= 16
    | None -> false

/// Fetches linked pictures (a few at a time) and drops those that can't be fetched or drawn, and icons.
let resolveImages (fetch: string -> Task<byte[] option>) (doc: Document) : Task<Document> =
    task {
        let links = doc.Blocks |> List.choose (function Block.Image (Link url, _, _) -> Some url | _ -> None) |> List.distinct
        let fetched = Collections.Concurrent.ConcurrentDictionary<string, byte[]>()
        for batch in links |> List.chunkBySize 4 do
            let! _ =
                batch
                |> List.map (fun url ->
                    task {
                        match! fetch url with
                        | Some bytes -> fetched.[url] <- bytes
                        | None -> ()
                    })
                |> Task.WhenAll
            ()
        let blocks =
            doc.Blocks
            |> List.choose (fun b ->
                match b with
                | Block.Image (Link url, alt, caption) ->
                    match fetched.TryGetValue url with
                    | true, bytes when isFigureImage bytes -> Some(Block.Image(Data bytes, alt, caption))
                    | _ when caption <> "" -> Some(Block.Paragraph caption) // the caption is still worth hearing
                    | _ -> None
                | Block.Image (Data bytes, _, caption) when not (isFigureImage bytes) ->
                    if caption <> "" then Some(Block.Paragraph caption) else None
                | b -> Some b)
        return { doc with Blocks = blocks }
    }

// ---------------------------------------------------------------------------------------------
// Tidying the block list
// ---------------------------------------------------------------------------------------------

let private captionRx = Regex(@"^\W*(Figure|Fig\.?|Table|Tab\.|Listing|Algorithm|Chart|Image|Photo|Illustration|Plate|Exhibit)\s*[A-Z]?\d+[a-z]?\s*[:.|—–-]", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// True for text that reads like a caption: "Figure 3: ...", "Table 2. ...".
let isCaption (text: string) = captionRx.IsMatch text

/// Gives captionless pictures and tables the "Figure N: ..." / "Table N: ..." paragraph right before or after
/// them (how word processors, OCR and many web pages write captions).
let attachCaptions (blocks: Block list) : Block list =
    let arr = Array.ofList blocks
    let used = Collections.Generic.HashSet<int>()
    let captionAt k =
        if k >= 0 && k < arr.Length && not (used.Contains k) then
            match arr.[k] with
            | Block.Paragraph t when isCaption t -> Some t
            | _ -> None
        else None
    let isTable (t: string) = Regex.IsMatch(t, @"^\W*(Table|Tab\.)", RegexOptions.IgnoreCase)
    for i in 0 .. arr.Length - 1 do
        match arr.[i] with
        | Block.Image (src, alt, "") ->
            // figure captions sit below; take one above only when nothing below fits
            match captionAt (i + 1) |> Option.filter (isTable >> not), captionAt (i - 1) |> Option.filter (isTable >> not) with
            | Some c, _ -> used.Add(i + 1) |> ignore; arr.[i] <- Block.Image(src, alt, c)
            | None, Some c -> used.Add(i - 1) |> ignore; arr.[i] <- Block.Image(src, alt, c)
            | _ -> ()
        | Block.Table (rows, "") ->
            // table captions sit above
            match captionAt (i - 1) |> Option.filter isTable, captionAt (i + 1) |> Option.filter isTable with
            | Some c, _ -> used.Add(i - 1) |> ignore; arr.[i] <- Block.Table(rows, c)
            | None, Some c -> used.Add(i + 1) |> ignore; arr.[i] <- Block.Table(rows, c)
            | _ -> ()
        | _ -> ()
    arr |> Array.indexed |> Array.filter (fun (k, _) -> not (used.Contains k)) |> Array.map snd |> List.ofArray

// ---------------------------------------------------------------------------------------------
// Sentences
// ---------------------------------------------------------------------------------------------

let private mathSpanRx = Regex(@"\$\$(.+?)\$\$|\$([^$]+?)\$", RegexOptions.Compiled ||| RegexOptions.Singleline)

/// The inline formulas of a text, in order.
let mathSpans (text: string) =
    [ for m in mathSpanRx.Matches text -> (if m.Groups.[1].Success then m.Groups.[1].Value else m.Groups.[2].Value).Trim() ]

/// Text with its $math$ read aloud.
let speakInline (text: string) =
    mathSpanRx.Replace(text, fun m -> " " + MathText.speakLatex (if m.Groups.[1].Success then m.Groups.[1].Value else m.Groups.[2].Value) + " ")

/// The sentences of a paragraph. A formula never splits (its spaces and dots aren't sentence ends).
let sentences (text: string) : string list =
    let spans = Collections.Generic.List<string>()
    // formulas become placeholder words while splitting
    let masked = mathSpanRx.Replace(text, fun m -> spans.Add m.Value; sprintf "\u0001%d\u0002" (spans.Count - 1))
    let restore (s: string) = Regex.Replace(s, "\u0001(\\d+)\u0002", fun m -> spans.[int m.Groups.[1].Value])
    let words = masked.Split([| ' '; '\n'; '\t'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    let out = Collections.Generic.List<string>()
    let cur = Collections.Generic.List<string>()
    for i in 0 .. words.Length - 1 do
        cur.Add words.[i]
        let next = if i + 1 < words.Length then words.[i + 1] else ""
        // very long run-ons (lists, legal text) are cut at a semicolon so clips stay short
        let tooLong = cur.Count >= 70 && (words.[i].EndsWith ";" || words.[i].EndsWith ",")
        if (next <> "" && SpeechText.endsSentence words.[i] next) || tooLong then
            out.Add(String.Join(" ", cur))
            cur.Clear()
    if cur.Count > 0 then out.Add(String.Join(" ", cur))
    out |> Seq.map (restore >> MathText.tidy) |> Seq.filter (fun s -> s <> "") |> List.ofSeq

/// A formula worth showing: more than a lone letter or number.
let private substantial (latex: string) =
    latex.Length > 2 && (latex.Contains '\\' || latex.Contains '^' || latex.Contains '_' || latex.Contains '=' || latex.Length > 6)

// ---------------------------------------------------------------------------------------------
// Blocks to units and visuals
// ---------------------------------------------------------------------------------------------

let private tagRx = Regex(@"\\tag\*?\s*\{([^}]*)\}", RegexOptions.Compiled)
let private numberedHeadingRx = Regex(@"^((\d{1,2}(\.\d{1,2})*|[A-Z](\.\d{1,2})*)\.?)\s+(.*)$", RegexOptions.Compiled)
let private chapterRx = Regex(@"^(chapter|part|book|section|appendix|lesson|unit|slide)\b", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// How a heading is said: "Section 3. Method." for a numbered one, as it is otherwise.
let private spokenHeading (text: string) =
    let m = numberedHeadingRx.Match text
    if m.Success && m.Groups.[5].Value.Length > 0 && not (chapterRx.IsMatch text) then
        let num = m.Groups.[2].Value
        let word = if Char.IsLetter num.[0] && num.Length = 1 then "Appendix" else "Section"
        sprintf "%s %s. %s." word num (m.Groups.[5].Value.TrimEnd('.'))
    else text.TrimEnd('.', ':') + "."

/// A table as markdown, for the narrator and for answering questions.
let tableMarkdown (rows: string list list) =
    match rows with
    | [] -> ""
    | header :: body ->
        let line (cells: string list) = "| " + String.Join(" | ", cells |> List.map (fun c -> c.Replace("|", "/").Replace("\n", " "))) + " |"
        let sep = "|" + String.Join("|", header |> List.map (fun _ -> "---")) + "|"
        String.Join("\n", line header :: sep :: (body |> List.map line))

/// Characters of running text per page, for documents without pages of their own.
let charsPerPage = 3000

/// Turns a document into units and visuals, and the pictures to draw for its visuals (visual id -> picture).
let analyze (doc: Document) : Analysis * (string * Picture) list =
    let units = ResizeArray<SourceUnit>()
    let visuals = ResizeArray<Visual>()
    let pictures = ResizeArray<string * Picture>()
    let sections = ResizeArray<string>([ doc.Title ])
    let mutable section = 0
    let mutable counter = 0
    let nextId prefix =
        counter <- counter + 1
        sprintf "%s%d" prefix counter
    let paged = doc.Blocks |> List.exists (fun b -> b = Block.Break)
    let mutable page = 0
    let mutable started = false // the document has content on the current page
    let mutable breaks = 0
    let mutable chars = 0
    let usedIds = Collections.Generic.HashSet<string>()
    let mutable pictureCount = 0
    let visualId (kind: VisualKind) (number: string option) =
        // numbered figures and tables get the ids references resolve to ("Fig2", "Tab1")
        match number with
        | Some n when usedIds.Add(Ocr.figureId kind n) -> Ocr.figureId kind n
        | _ ->
            pictureCount <- pictureCount + 1
            let id = sprintf "%s%d" (if kind = VisualKind.Table then "Grid" else "Pic") pictureCount
            usedIds.Add id |> ignore
            id
    let advance (text: string) =
        started <- true
        if not paged then
            chars <- chars + text.Length
            page <- chars / charsPerPage

    let addUnit kind text spoken visual paragraphEnd prefix =
        let id = nextId prefix
        units.Add
            { Id = id; Kind = kind; Text = text; Spoken = spoken; Visual = visual
              Page = page; Section = section; ParagraphEnd = paragraphEnd }
        id

    /// A paragraph's sentences; the first may carry a visual (a caption carries its figure).
    let addText (text: string) (firstVisual: string option) =
        let ss = sentences text
        ss |> List.iteri (fun k s ->
            let spoken = SpeechText.forSpeech (speakInline s)
            let visual =
                if k = 0 && firstVisual.IsSome then firstVisual
                else
                    // inline math worth seeing is typeset, up to three formulas
                    let spans = mathSpans s |> List.filter substantial |> List.truncate 3
                    if spans.IsEmpty then None
                    else
                        let id = sprintf "S%d" (counter + 1)
                        visuals.Add { Id = id; Kind = VisualKind.Inline; Page = page; Parts = [||]; EqNumber = None; RawText = s; Latex = Some(String.Join("\n", spans)) }
                        pictures.Add((id, Formula(String.Join("\n", spans))))
                        Some id
            if spoken |> Seq.exists Char.IsLetterOrDigit || visual.IsSome then
                let spoken = if spoken |> Seq.exists Char.IsLetterOrDigit then spoken else "It is shown on screen."
                addUnit UnitKind.Sentence s spoken visual (k = ss.Length - 1) "S" |> ignore)
        advance text

    units.Add
        { Id = "T0"; Kind = UnitKind.Title; Text = doc.Title; Spoken = SpeechText.forSpeech doc.Title
          Visual = None; Page = 0; Section = 0; ParagraphEnd = true }
    let normal (s: string) = Regex.Replace(s.ToLowerInvariant(), @"\W+", "")

    for b in doc.Blocks do
        match b with
        | Block.Break ->
            // every break starts a page, even after an empty one; a leading break doesn't
            if paged && (started || breaks > 0) then page <- page + 1
            breaks <- breaks + 1
            started <- false
        | Block.Heading (_, text) ->
            let text = MathText.tidy text
            // the title is read once, even when the document repeats it as its first heading
            if text <> "" && not (units.Count = 1 && normal text = normal doc.Title) then
                section <- section + 1
                sections.Add text
                addUnit UnitKind.Heading text (SpeechText.forSpeech (spokenHeading (speakInline text))) None true "H" |> ignore
                advance text
            else advance text
        | Block.Paragraph text when units.Count = 1 && normal text = normal doc.Title -> advance text // the title again
        | Block.Paragraph text
        | Block.Item text -> addText text None
        | Block.Math (latex, number) ->
            let number = number |> Option.orElse (let m = tagRx.Match latex in if m.Success then Some(m.Groups.[1].Value.Trim()) else None)
            let latex = MathText.compactLatex ((tagRx.Replace(latex, "")).Trim())
            if latex <> "" then
                let id = sprintf "E%d" (counter + 1)
                visuals.Add { Id = id; Kind = VisualKind.Equation; Page = page; Parts = [||]; EqNumber = number; RawText = latex; Latex = Some latex }
                pictures.Add((id, Formula latex))
                let said = (MathText.speakLatex latex).TrimEnd(',', '.', ';', ' ')
                let name = match number with Some n -> sprintf "Equation %s" n | None -> "In symbols"
                let spoken = if said.Length <= 300 then sprintf "%s: %s." name said else sprintf "%s is on screen." (if number.IsSome then name else "An equation")
                addUnit UnitKind.Equation latex (SpeechText.forSpeech spoken) (Some id) false "E" |> ignore
                advance latex
        | Block.Image (Data bytes, alt, caption) ->
            let number = Ocr.captionOf caption |> Option.filter (fun (k, _) -> k = VisualKind.Figure) |> Option.map snd
            let id = visualId VisualKind.Figure number
            let raw = if caption <> "" then caption elif alt <> "" then "Picture: " + alt else "Picture"
            visuals.Add { Id = id; Kind = VisualKind.Figure; Page = page; Parts = [||]; EqNumber = number; RawText = raw; Latex = None }
            pictures.Add((id, Bitmap bytes))
            if caption <> "" then addText caption (Some id)
            else
                let text = if alt <> "" then "Picture: " + MathText.tidy alt else "Picture."
                addUnit UnitKind.Sentence text (SpeechText.forSpeech (if alt <> "" then text else "A picture is shown.")) (Some id) true "S" |> ignore
                advance text
        | Block.Image (Region r, _, caption) ->
            let number = Ocr.captionOf caption |> Option.filter (fun (k, _) -> k = VisualKind.Figure) |> Option.map snd
            let id = visualId VisualKind.Figure number
            visuals.Add { Id = id; Kind = VisualKind.Figure; Page = r.Page; Parts = [| r |]; EqNumber = number
                          RawText = (if caption <> "" then caption else "Picture"); Latex = None }
            if caption <> "" then addText caption (Some id)
            else addUnit UnitKind.Sentence "Picture." (SpeechText.forSpeech "A picture is shown.") (Some id) true "S" |> ignore
            advance caption
        | Block.Image (Link _, _, caption) ->
            // not fetched (see resolveImages): the caption is still read
            if caption <> "" then addText caption None
        | Block.Table (rows, caption) ->
            let rows = rows |> List.filter (List.exists (fun c -> c.Trim() <> ""))
            if not rows.IsEmpty then
                let number = Ocr.captionOf caption |> Option.filter (fun (k, _) -> k = VisualKind.Table) |> Option.map snd
                let id = visualId VisualKind.Table number
                let md = tableMarkdown rows
                visuals.Add { Id = id; Kind = VisualKind.Table; Page = page; Parts = [||]; EqNumber = number
                              RawText = (if caption <> "" then caption + "\n" else "") + md; Latex = None }
                pictures.Add((id, Grid rows))
                if caption <> "" then addText caption (Some id)
                else
                    let header = rows.Head |> List.map (fun c -> c.Trim()) |> List.filter ((<>) "") |> List.truncate 6
                    let spoken =
                        if header.IsEmpty || rows.Length < 2 then "A table is shown."
                        else sprintf "A table with %d rows, with columns %s." (rows.Length - 1) (String.Join(", ", header))
                    addUnit UnitKind.Sentence "Table." (SpeechText.forSpeech spoken) (Some id) true "S" |> ignore
                advance md
        | Block.Code (text, caption) ->
            if text.Trim() <> "" then
                let id = sprintf "E%d" (counter + 1)
                let number = Regex.Match(caption, @"^\W*(?:Algorithm|Listing)\s*([A-Z]?\d+)", RegexOptions.IgnoreCase) |> fun m -> if m.Success then Some m.Groups.[1].Value else None
                visuals.Add { Id = id; Kind = VisualKind.Algorithm; Page = page; Parts = [||]; EqNumber = number; RawText = text; Latex = None }
                pictures.Add((id, Listing text))
                let spoken = (if caption <> "" then caption.TrimEnd('.') + ". " else "") + "The listing is shown on screen."
                addUnit UnitKind.Equation text (SpeechText.forSpeech (speakInline spoken)) (Some id) true "E" |> ignore
                advance text

    let analysis =
        { Title = doc.Title
          Source = doc.Source
          PageCount = page + 1
          Sections = sections.ToArray()
          Units = units.ToArray()
          Visuals = visuals.ToArray() }
    analysis, List.ofSeq pictures

// ---------------------------------------------------------------------------------------------
// Markdown text, kept for answering questions
// ---------------------------------------------------------------------------------------------

/// The document as markdown with LaTeX math: what Ask and the card maker read.
let toMarkdown (doc: Document) (analysis: Analysis) : string =
    let sb = StringBuilder()
    sb.Append("# ").AppendLine(doc.Title).AppendLine() |> ignore
    // captions name their visual the way the analysis did, so answers can point at it
    let byCaption = analysis.Visuals |> Array.map (fun v -> v.RawText, v) |> Array.distinctBy fst |> dict
    let mutable page = 1
    for b in doc.Blocks do
        match b with
        | Block.Break ->
            page <- page + 1
            sb.AppendFormat("\n<!-- page {0} -->\n\n", page) |> ignore
        | Block.Heading (level, text) -> sb.Append(String('#', min 6 (max 2 (level + 1)))).Append(' ').AppendLine(text).AppendLine() |> ignore
        | Block.Paragraph text -> sb.AppendLine(text).AppendLine() |> ignore
        | Block.Item text -> sb.Append("- ").AppendLine(text) |> ignore
        | Block.Math (latex, number) ->
            sb.Append("$$").Append(latex) |> ignore
            number |> Option.iter (fun n -> sb.Append(@" \tag{").Append(n).Append("}") |> ignore)
            sb.AppendLine("$$").AppendLine() |> ignore
        | Block.Image (_, alt, caption) ->
            let raw = if caption <> "" then caption elif alt <> "" then "Picture: " + alt else "Picture"
            let id = match byCaption.TryGetValue raw with | true, v -> v.Id | _ -> "picture"
            sb.AppendFormat("[{0}: {1}]", id, raw).AppendLine().AppendLine() |> ignore
        | Block.Table (rows, caption) ->
            if caption <> "" then sb.AppendLine(caption).AppendLine() |> ignore
            sb.AppendLine(tableMarkdown rows).AppendLine() |> ignore
        | Block.Code (text, caption) ->
            if caption <> "" then sb.AppendLine(caption).AppendLine() |> ignore
            sb.AppendLine("```").AppendLine(text).AppendLine("```").AppendLine() |> ignore
    sb.ToString()
