/// Turns a PDF into source units in reading order: title, headings, sentences and display equations,
/// with page regions for everything that should be shown as an image.
module PaperReader.Core.Layout

open System
open System.Text
open System.Text.RegularExpressions
open UglyToad.PdfPig
open UglyToad.PdfPig.Content
open UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor

// ---------------------------------------------------------------------------------------------
// Geometry primitives. PDF coordinates: y grows upwards.
// ---------------------------------------------------------------------------------------------

type Glyph =
    { Value: string
      Left: float
      Right: float
      Bottom: float
      Top: float
      Baseline: float
      /// Pen position before and after the glyph (advance), for measuring word spacing.
      StartX: float
      EndX: float
      Size: float
      Font: string
      IsMath: bool
      IsBold: bool }

type Word =
    { Glyphs: Glyph[]
      Left: float
      Right: float
      Bottom: float
      Top: float
      Baseline: float
      Size: float }

    member w.Text = w.Glyphs |> Array.map (fun g -> g.Value) |> String.Concat

[<RequireQualifiedAccess>]
type Region =
    | Full
    | Left
    | Right

[<ReferenceEquality>]
type Line =
    { Page: int
      Region: Region
      Words: Word[]
      Left: float
      Right: float
      Bottom: float
      Top: float
      Baseline: float
      Size: float }

    member l.Glyphs = l.Words |> Array.collect (fun w -> w.Glyphs)
    member l.Text = l.Words |> Array.map (fun w -> w.Text) |> String.concat " "

[<RequireQualifiedAccess>]
type LineKind =
    | Text
    | Heading of level: int
    | Display
    | Fragment
    | Drop of reason: string

type ClassifiedLine = { Line: Line; Kind: LineKind }

let private median (xs: float seq) =
    let a = Seq.toArray xs |> Array.sort
    if a.Length = 0 then 0.0 else a.[a.Length / 2]

let private bounds (glyphs: Glyph seq) =
    let g = Seq.toArray glyphs
    (g |> Array.minBy (fun x -> x.Left)).Left,
    (g |> Array.maxBy (fun x -> x.Right)).Right,
    (g |> Array.minBy (fun x -> x.Bottom)).Bottom,
    (g |> Array.maxBy (fun x -> x.Top)).Top

let private mkWord (glyphs: Glyph[]) =
    let glyphs = glyphs |> Array.sortBy (fun g -> g.StartX)
    let l, r, b, t = bounds glyphs
    let maxSize = glyphs |> Array.map (fun g -> g.Size) |> Array.max
    // Baseline of the word = baseline of its full-size glyphs, so subscripts don't drag it down.
    let mains = glyphs |> Array.filter (fun g -> g.Size >= maxSize * 0.85)
    { Glyphs = glyphs
      Left = l
      Right = r
      Bottom = b
      Top = t
      Baseline = median (mains |> Seq.map (fun g -> g.Baseline))
      Size = maxSize }

/// Splits a word where two non-math glyphs are separated by a visible gap (tightly justified text
/// sometimes has no space glyphs, and nearest-neighbour grouping then glues words together).
let private splitAtGaps (glyphs: Glyph[]) : Glyph[] list =
    let gs = glyphs |> Array.sortBy (fun g -> g.StartX)
    let parts = ResizeArray<ResizeArray<Glyph>>([ ResizeArray [ gs.[0] ] ])
    for k in 1 .. gs.Length - 1 do
        let a = gs.[k - 1]
        let b = gs.[k]
        let gap = b.StartX - a.EndX
        let textPair = not a.IsMath && not b.IsMath && a.Size >= b.Size * 0.85 && b.Size >= a.Size * 0.85
        if textPair && gap > 0.15 * a.Size then parts.Add(ResizeArray [ b ])
        else parts.[parts.Count - 1].Add b
    [ for p in parts -> p.ToArray() ]

let private mkLine page region (words: Word seq) =
    let words = words |> Seq.sortBy (fun w -> w.Left) |> Seq.toArray
    let glyphs = words |> Array.collect (fun w -> w.Glyphs)
    let l, r, b, t = bounds glyphs
    let size = median (words |> Seq.map (fun w -> w.Size))
    let mains = words |> Array.filter (fun w -> w.Size >= size * 0.85)
    { Page = page
      Region = region
      Words = words
      Left = l
      Right = r
      Bottom = b
      Top = t
      Baseline = median (mains |> Seq.map (fun w -> w.Baseline))
      Size = size }

// ---------------------------------------------------------------------------------------------
// Page reading: glyphs -> words -> lines (with columns) in reading order.
// ---------------------------------------------------------------------------------------------

let private toGlyph (l: Letter) =
    let size = if l.PointSize > 0.0 then l.PointSize else 10.0
    let r = l.BoundingBox
    let hasBox = r.Height > 0.01 && r.Width > 0.01
    let baseline = l.StartBaseLine.Y
    { Value = l.Value
      Left = if hasBox then r.Left else l.StartBaseLine.X
      Right = if hasBox then r.Right else l.StartBaseLine.X + size * 0.5
      Bottom = if hasBox then r.Bottom else baseline - size * 0.22
      Top = if hasBox then r.Top else baseline + size * 0.72
      Baseline = baseline
      StartX = l.StartBaseLine.X
      EndX = l.EndBaseLine.X
      Size = size
      Font = l.FontName
      IsMath = MathText.isMathFont l.FontName || MathText.isMathSymbol l.Value
      IsBold = MathText.isBoldFont l.FontName }

/// Finds the x of a two-column gutter, if the page has one.
let private findGutter (pageWidth: float) (words: Word[]) (body: float) =
    let textWords = words |> Array.filter (fun w -> w.Size >= body * 0.8 && w.Size <= body * 1.3)
    if textWords.Length < 60 then None
    else
        let crossing x = textWords |> Array.sumBy (fun w -> if w.Left < x && w.Right > x then 1 else 0)
        let candidates = [| for x in pageWidth * 0.3 .. 1.0 .. pageWidth * 0.7 -> x, crossing x |]
        let best = candidates |> Array.minBy snd |> snd
        let limit = max 2 (textWords.Length / 50)
        if best > limit then None
        else
            // centre of the widest run of best-scoring positions
            let runs = ResizeArray<float * float>()
            let mutable start = nan
            for (x, c) in candidates do
                if c <= best then (if Double.IsNaN start then start <- x)
                elif not (Double.IsNaN start) then
                    runs.Add(start, x - 1.0)
                    start <- nan
            if not (Double.IsNaN start) then runs.Add(start, pageWidth * 0.7)
            let (a, b) = runs |> Seq.maxBy (fun (a, b) -> b - a)
            let split = (a + b) / 2.0
            let left = textWords |> Array.filter (fun w -> w.Right <= split) |> Array.length
            let right = textWords |> Array.filter (fun w -> w.Left >= split) |> Array.length
            let n = float textWords.Length
            if float left > n * 0.2 && float right > n * 0.2 then Some split else None

/// Groups words into lines for one page, splitting rows at the gutter, and returns them in reading order.
let private pageLines (pageIndex: int) (pageWidth: float) (words: Word[]) (body: float) =
    let gutter = findGutter pageWidth words body
    let isScript (w: Word) = w.Size < body * 0.8
    let mainWords = words |> Array.filter (not << isScript) |> Array.sortByDescending (fun w -> w.Baseline)
    // 1. rows of full-size words by baseline
    let rows = ResizeArray<ResizeArray<Word>>()
    for w in mainWords do
        let fits (row: ResizeArray<Word>) =
            let b = median (row |> Seq.map (fun x -> x.Baseline))
            abs (b - w.Baseline) <= 0.3 * max w.Size body
        match rows |> Seq.tryFindBack fits with
        | Some row -> row.Add w
        | None -> rows.Add(ResizeArray [ w ])
    // 2. split rows at wide gaps (figure labels next to text), then into regions at the gutter
    let rows =
        [ for row in rows do
              let sorted = row |> Seq.sortBy (fun w -> w.Left) |> Seq.toArray
              let mutable current = ResizeArray<Word>([ sorted.[0] ])
              for k in 1 .. sorted.Length - 1 do
                  let gap = sorted.[k].Left - sorted.[k - 1].Right
                  let crossesGutter = match gutter with Some g -> sorted.[k - 1].Right <= g && sorted.[k].Left >= g | None -> false
                  // Only split where the text on each side is set differently (a figure's labels beside a
                  // paragraph); display equations have wide internal gaps but share their fonts.
                  let prose (ws: Word seq) =
                      let ws = Seq.toArray ws
                      let gs = ws |> Array.collect (fun w -> w.Glyphs)
                      let math = gs |> Array.filter (fun g -> g.IsMath) |> Array.length
                      let words =
                          ws |> Array.filter (fun w ->
                              let t = w.Text.Trim('.', ',', ';', ':')
                              t.Length >= 3 && t |> Seq.forall Char.IsLetter && not (w.Glyphs |> Array.exists (fun g -> g.IsMath)))
                      words.Length >= 3 && float math < 0.15 * float (max 1 gs.Length)
                  let rest = sorted |> Array.skip k
                  let eqNumber = rest.Length = 1 && Regex.IsMatch(rest.[0].Text, @"^\([\w.]+\)[.,]?$")
                  let differs = not eqNumber && (prose current || prose rest)
                  if gap > body * 2.5 && not crossesGutter && differs then
                      yield current
                      current <- ResizeArray<Word>()
                  current.Add sorted.[k]
              yield current ]
    let lines = ResizeArray<Line>()
    for row in rows do
        let sorted = row |> Seq.sortBy (fun w -> w.Left) |> Seq.toArray
        match gutter with
        | None -> lines.Add(mkLine pageIndex Region.Full sorted)
        | Some split ->
            let left = sorted |> Array.filter (fun w -> w.Right <= split)
            let right = sorted |> Array.filter (fun w -> w.Left >= split)
            let crossingWords = sorted |> Array.filter (fun w -> w.Left < split && w.Right > split)
            // A row is full-width if a word crosses the gutter with no large gap next to it.
            let gapAround (w: Word) =
                let before = left |> Array.filter (fun x -> x.Right <= w.Left) |> Array.map (fun x -> w.Left - x.Right)
                let after = right |> Array.filter (fun x -> x.Left >= w.Right) |> Array.map (fun x -> x.Left - w.Right)
                let minOr d a = if Array.isEmpty a then d else Array.min a
                min (minOr 0.0 before) (minOr 0.0 after)
            if crossingWords.Length > 0 && crossingWords |> Array.exists (fun w -> gapAround w < body * 1.5) then
                lines.Add(mkLine pageIndex Region.Full sorted)
            else
                if left.Length > 0 then lines.Add(mkLine pageIndex Region.Left left)
                if right.Length > 0 then lines.Add(mkLine pageIndex Region.Right right)
                // a lone crossing word with big gaps (rare): attach to whichever side its centre is on
                for w in crossingWords do
                    let region = if (w.Left + w.Right) / 2.0 < split then Region.Left else Region.Right
                    lines.Add(mkLine pageIndex region [ w ])
    // 3. attach sub/superscripts to the nearest line whose band contains them
    let orphans = ResizeArray<Word>()
    let lines = lines.ToArray() |> Array.map (fun l -> l, ResizeArray<Word>(l.Words))
    for w in words |> Array.filter isScript do
        let candidates =
            lines
            |> Array.filter (fun (l, _) ->
                let d = w.Baseline - l.Baseline
                d > -0.5 * l.Size && d < 0.75 * l.Size
                && w.Left >= l.Left - body * 1.5 && w.Right <= l.Right + body * 1.5)
        if candidates.Length = 0 then orphans.Add w
        else
            let (_, ws) = candidates |> Array.minBy (fun (l, _) -> abs (w.Baseline - (l.Baseline + 0.1 * l.Size)))
            ws.Add w
    // Tiny rows made only of math glyphs (a raised radical sign, the limits of a sum) belong to a nearby line.
    let lines =
        let isTiny (l: Line, _) =
            let gs = l.Words |> Array.collect (fun w -> w.Glyphs)
            gs.Length <= 4 && gs |> Array.forall (fun g -> g.IsMath || Char.IsDigit g.Value.[0] || "()=+-−,".Contains g.Value)
        let tiny, normal = lines |> Array.partition isTiny
        // distance between the tiny row's box and a line's box (0 when they overlap vertically)
        let distance (t: Line) (l: Line) =
            if t.Top < l.Bottom then l.Bottom - t.Top
            elif t.Bottom > l.Top then t.Bottom - l.Top
            else -(min t.Top l.Top - max t.Bottom l.Bottom) // negative: amount of overlap
        for (t, _) in tiny do
            let target =
                normal
                |> Array.filter (fun (l, _) ->
                    l.Region = t.Region && distance t l <= 0.6 * body
                    && t.Left >= l.Left - body && t.Right <= l.Right + body)
                |> Array.sortBy (fun (l, _) -> distance t l)
                |> Array.tryHead
            match target with
            | Some (_, ws) -> ws.AddRange t.Words
            | None -> orphans.AddRange t.Words
        normal
    // Orphans (small text belonging to no line: figure labels, stray limits) are left out on purpose.
    // Kept as lines they interleave with equation rows and break display blocks apart; the crop
    // padding still covers limits that sit just outside an equation's rows.
    let all = [| for (l, ws) in lines -> mkLine pageIndex l.Region ws |]
    // 4. reading order: full-width lines cut the page into bands; within a band, left column then right.
    let full = all |> Array.filter (fun l -> l.Region = Region.Full) |> Array.sortByDescending (fun l -> l.Baseline)
    let cols = all |> Array.filter (fun l -> l.Region <> Region.Full)
    let ordered = ResizeArray<Line>()
    let mutable upper = Double.PositiveInfinity
    let emitBand lower =
        let inBand = cols |> Array.filter (fun l -> l.Baseline < upper && l.Baseline >= lower)
        for r in [ Region.Left; Region.Right ] do
            inBand |> Array.filter (fun l -> l.Region = r) |> Array.sortByDescending (fun l -> l.Baseline) |> ordered.AddRange
    for f in full do
        emitBand f.Baseline
        ordered.Add f
        upper <- f.Baseline
    emitBand Double.NegativeInfinity
    ordered.ToArray()

// ---------------------------------------------------------------------------------------------
// Line classification.
// ---------------------------------------------------------------------------------------------

let private eqNumberRx = Regex(@"^\((\d+[a-z]?|[A-Z]\.?\d+[a-z]?|\d+\.\d+[a-z]?)\)[.,]?$", RegexOptions.Compiled)
let private numberedHeadingRx = Regex(@"^(\d{1,2}(\.\d{1,2}){0,3}\.?|[A-Z](\.\d{1,2}){0,3}\.?|[IVX]{1,4}\.)\s+\S", RegexOptions.Compiled)
let private knownHeadingRx =
    Regex(@"^((\d{1,2}|[A-Z]|[IVX]{1,4})\.?\s+)?(abstract|introduction|related work|background|preliminaries|method|methods|methodology|approach|model|experiments?|experimental setup|results|evaluation|discussion|limitations|conclusions?|conclusion and future work|future work|acknowledge?ments?|references|bibliography|appendix|appendices|supplementary material)$",
          RegexOptions.Compiled ||| RegexOptions.IgnoreCase)
let private referencesRx = Regex(@"^((\d{1,2}|[IVX]{1,4})\.?\s+)?(references|bibliography|literature cited|works cited)$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)
let private appendixRx = Regex(@"^(appendix|appendices|supplementary|[A-Z](\.\d+)*\.?\s+[A-Z])", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)
let private mathWords = set [ "sin"; "cos"; "tan"; "log"; "exp"; "max"; "min"; "arg"; "sup"; "inf"; "lim"; "det"; "tr"; "var"; "cov"; "softmax"; "diag"; "argmax"; "argmin"; "s.t."; "subject"; "for"; "and"; "if"; "otherwise"; "where" ]

let isEqNumber (s: string) = eqNumberRx.IsMatch s
let private algorithmRx = Regex(@"^Algorithm\s*(\d+)\s*[:.]", RegexOptions.Compiled)
let private algorithmStepRx = Regex(@"^(Require|Input|Output|Ensure|Initiali[sz]e|Parameters?|Given|procedure|function|for|while|repeat|if|\d+\s*:)", RegexOptions.Compiled)

let private proseWordCount (l: Line) =
    l.Words
    |> Array.sumBy (fun w ->
        let t = w.Text.Trim('.', ',', ';', ':', '(', ')', '"', '\'')
        let letters = t |> Seq.filter Char.IsLetter |> Seq.length
        if not (w.Glyphs |> Array.exists (fun g -> g.IsMath))
           && letters >= 3 && float letters >= 0.8 * float t.Length
           && not (mathWords.Contains(t.ToLowerInvariant())) then 1 else 0)

let private nonSpace (l: Line) = l.Glyphs |> Array.filter (fun g -> not (String.IsNullOrWhiteSpace g.Value))

let private isDisplayMathAt (columnLeft: float) (body: float) (l: Line) =
    let gs = nonSpace l
    if gs.Length = 0 then false
    else
        let math = gs |> Array.filter (fun g -> g.IsMath) |> Array.length
        let frac = float math / float gs.Length
        let prose = proseWordCount l
        let hasNumber = l.Words |> Array.exists (fun w -> isEqNumber w.Text)
        let hasRelation = gs |> Array.exists (fun g -> "=≤≥<>≈∝←→:=∈".Contains g.Value)
        // Unnumbered display math is indented or centred; a flush-left math line ends a paragraph.
        let indented = l.Left > columnLeft + body * 1.5
        (hasNumber && math >= 2 && prose <= 3)
        || (indented && frac >= 0.4 && prose <= 2)
        || (indented && hasRelation && math >= 2 && prose = 0 && gs.Length <= 60)

let private isFragment (l: Line) =
    let gs = nonSpace l
    gs.Length <= 4 && proseWordCount l = 0

let private upperFraction (s: string) =
    let letters = s |> Seq.filter Char.IsLetter |> Seq.toArray
    if letters.Length = 0 then 0.0
    else float (letters |> Array.filter Char.IsUpper |> Array.length) / float letters.Length

let private fontFamily (font: string) =
    (MathText.baseFontName font).TrimEnd([| '0' .. '9' |])

// ---------------------------------------------------------------------------------------------
// Text rendering of words: LaTeX-ish marked text for the model, and spoken text for offline TTS.
// ---------------------------------------------------------------------------------------------

type private Role =
    | Normal
    | Sub
    | Sup

/// Big operators and tall delimiters sit off the baseline by design; they are never scripts.
let private isLargeSymbol (g: Glyph) =
    (MathText.baseFontName g.Font).ToUpperInvariant().Contains "CMEX" || "∑∏∫∮⋃⋂⨁⨂()[]{}⟨⟩|‖√".Contains g.Value

let private roleOf (line: Line) (g: Glyph) =
    let small = g.Size < line.Size * 0.85 && not (isLargeSymbol g)
    let rise = g.Baseline - line.Baseline
    if small && rise > 0.12 * line.Size then Sup
    elif small && rise < -0.05 * line.Size then Sub
    elif rise > 0.3 * line.Size && g.IsMath && not (isLargeSymbol g) then Sup
    else Normal

let private footnoteMark (s: string) =
    s |> Seq.forall (fun c -> Char.IsDigit c || "*†‡§¶∗⋆,".Contains c)

/// Renders a word both as marked-up text (x_{i}^{2}) and as speech ("x sub i squared").
let private renderWord (line: Line) (w: Word) =
    let mathy = w.Glyphs |> Array.exists (fun g -> g.IsMath)
    let runs = ResizeArray<Role * Glyph list>()
    for g in w.Glyphs do
        let role = roleOf line g
        if runs.Count > 0 && fst runs.[runs.Count - 1] = role then
            let (r, gs) = runs.[runs.Count - 1]
            runs.[runs.Count - 1] <- (r, gs @ [ g ])
        else runs.Add(role, [ g ])
    let text = StringBuilder()
    let spoken = StringBuilder()
    let speakRun (gs: Glyph list) =
        gs |> List.map (fun g -> if g.IsMath || mathy then MathText.speakGlyph g.Value else g.Value) |> String.Concat
    let mutable previous = Normal
    for (role, gs) in runs do
        let raw = gs |> List.map (fun g -> g.Value) |> String.Concat
        match role with
        | Normal ->
            text.Append raw |> ignore
            spoken.Append(speakRun gs) |> ignore
        | Sub when mathy ->
            text.Append("_{").Append(raw).Append("}") |> ignore
            spoken.Append(" sub ").Append(speakRun gs).Append(" ") |> ignore
        | Sup when mathy ->
            text.Append("^{").Append(raw).Append("}") |> ignore
            // after a subscript it is usually the upper limit of a sum or product
            if previous = Sub then spoken.Append(" to ").Append(speakRun gs).Append(" ") |> ignore
            else spoken.Append(MathText.speakSuperscript (speakRun gs)) |> ignore
        | Sup when footnoteMark raw -> () // footnote or affiliation marker
        | _ ->
            text.Append raw |> ignore
            spoken.Append raw |> ignore
        previous <- role
    text.ToString(), spoken.ToString()

// ---------------------------------------------------------------------------------------------
// Document analysis.
// ---------------------------------------------------------------------------------------------

type private Placed = { Word: Word; Line: Line }

/// A line that opens a figure or table caption: "Figure 3: …", "Fig. 3.", "Table 2 |".
let private captionStartRx = Regex(@"^\W*(Figure|Fig\.?|Table)\s*[A-Z]?\d+[a-z]?\s*[:.|]", RegexOptions.Compiled)
let private captionWordRx = Regex(@"^(Figure|Fig\.?|Table)$", RegexOptions.Compiled)

/// Width and height of every page's crop box in points (the space PageRect coordinates live in).
let pageSizes (path: string) : (float * float)[] =
    use doc = PdfDocument.Open(path)
    [| for p in 1 .. doc.NumberOfPages ->
           let b = (doc.GetPage p).CropBox.Bounds
           b.Width, b.Height |]

/// Set by development tools to see how every line was classified.
let mutable trace: (string -> unit) option = None

let analyze (path: string) (progress: int -> int -> unit) : Analysis =
    use doc = PdfDocument.Open(path)
    let pageCount = doc.NumberOfPages
    // --- read glyphs and words for all pages
    let pages =
        [| for p in 1 .. pageCount do
               progress p pageCount
               let page = doc.GetPage p
               let crop = page.CropBox.Bounds
               let letters =
                   page.Letters
                   |> Seq.filter (fun l -> l.TextOrientation = TextOrientation.Horizontal && not (String.IsNullOrWhiteSpace l.Value))
                   |> Seq.toArray
               let words =
                   NearestNeighbourWordExtractor.Instance.GetWords(letters)
                   |> Seq.map (fun w -> w.Letters |> Seq.map toGlyph |> Seq.toArray)
                   |> Seq.filter (fun gs -> gs.Length > 0)
                   |> Seq.collect splitAtGaps
                   |> Seq.map mkWord
                   |> Seq.toArray
               yield (p - 1, crop, page.Rotation.Value, words) |]
    let allGlyphs = pages |> Array.collect (fun (_, _, _, ws) -> ws |> Array.collect (fun w -> w.Glyphs))
    let body =
        allGlyphs
        |> Array.filter (fun g -> not g.IsMath)
        |> Array.countBy (fun g -> Math.Round(g.Size * 2.0) / 2.0)
        |> fun a -> if a.Length = 0 then 10.0 else a |> Array.maxBy snd |> fst
    let familyShare =
        let total = float (max 1 allGlyphs.Length)
        allGlyphs |> Array.countBy (fun g -> fontFamily g.Font) |> Array.map (fun (f, c) -> f, float c / total) |> dict
    let lines =
        pages |> Array.collect (fun (i, crop, _, words) -> if words.Length = 0 then [||] else pageLines i crop.Width words body)
    let cropOf = pages |> Array.map (fun (_, crop, rot, _) -> crop, rot)

    // --- running headers/footers: text in the margins that repeats on many pages, or bare page numbers
    let marginKey (l: Line) =
        let (crop: UglyToad.PdfPig.Core.PdfRectangle), _ = cropOf.[l.Page]
        let h = crop.Height
        if l.Top > crop.Top - 0.09 * h || l.Bottom < crop.Bottom + 0.08 * h then
            Some(Regex.Replace(l.Text.ToLowerInvariant(), @"[\d\s]+", ""))
        else None
    let repeated =
        lines
        |> Array.choose (fun l -> marginKey l |> Option.map (fun k -> k, l.Page))
        |> Array.distinct
        |> Array.countBy fst
        |> Array.filter (fun (k, c) -> c >= max 2 (pageCount / 3))
        |> Array.map fst
        |> set

    // left edge of each column, and the vertical gaps around each line within its column
    let columnLeft =
        lines
        |> Array.groupBy (fun l -> l.Page, l.Region)
        |> Array.map (fun (k, ls) -> k, (ls |> Array.map (fun l -> l.Left) |> Array.sort |> fun a -> a.[a.Length / 10]))
        |> dict
    let gaps =
        let d = Collections.Generic.Dictionary<Line, float * float>(HashIdentity.Reference)
        for (_, ls) in lines |> Array.groupBy (fun l -> l.Page, l.Region) do
            let sorted = ls |> Array.sortByDescending (fun l -> l.Baseline)
            for k in 0 .. sorted.Length - 1 do
                let above = if k = 0 then 99.0 else sorted.[k - 1].Bottom - sorted.[k].Top
                let below = if k = sorted.Length - 1 then 99.0 else sorted.[k].Bottom - sorted.[k + 1].Top
                d.[sorted.[k]] <- (above, below)
        d
    let isDisplayMath (l: Line) = isDisplayMathAt columnLeft.[(l.Page, l.Region)] body l

    let classify (l: Line) : LineKind =
        let text = l.Text.Trim()
        let gs = nonSpace l
        let margin = marginKey l
        if gs.Length = 0 then LineKind.Drop "empty"
        elif margin.IsSome && (margin.Value.Length <= 3 || repeated.Contains margin.Value) then LineKind.Drop "header/footer"
        elif (let (crop: UglyToad.PdfPig.Core.PdfRectangle), _ = cropOf.[l.Page]
              l.Bottom < crop.Bottom + 0.22 * crop.Height && l.Size < body * 0.93 && not (isDisplayMath l)) then
            LineKind.Drop "footnote"
        elif isDisplayMath l then
            // math-looking text at a small size is a plot legend or axis label, not an equation
            if l.Size < body * 0.85 then LineKind.Drop "figure text" else LineKind.Display
        elif isFragment l then LineKind.Fragment
        else
            let bold = float (gs |> Array.filter (fun g -> g.IsBold) |> Array.length) >= 0.85 * float gs.Length
            let big = l.Size >= body * 1.12
            let words = l.Words.Length
            let upper = upperFraction text
            let endsLikeSentence = text.EndsWith "," || text.EndsWith ";" || (text.EndsWith "." && not (numberedHeadingRx.IsMatch text && words <= 2))
            let styled = bold || big || (upper >= 0.75 && (text |> Seq.filter Char.IsLetter |> Seq.length) >= 4)
            let numbered =
                numberedHeadingRx.IsMatch text
                && (let rest = Regex.Replace(text, @"^\S+\s+", "") in rest.Length > 0 && Char.IsUpper rest.[0])
            let above, below = gaps.[l]
            // unnumbered headings stand alone: extra space above and below (table header rows do not)
            let standalone = above >= body * 0.9 && below >= body * 0.45
            let hasDecimals = Regex.IsMatch(text, @"\s\d+\.\d+")
            let level =
                let m = Regex.Match(text, @"^[\dA-Z]+((\.\d+)*)")
                if numbered && m.Success then 1 + (m.Groups.[1].Value |> Seq.filter ((=) '.') |> Seq.length) else 1
            let flushLeft = l.Left <= columnLeft.[(l.Page, l.Region)] + body
            let capital = Char.IsUpper text.[0] || Char.IsDigit text.[0]
            if knownHeadingRx.IsMatch(text.TrimEnd('.', ':')) && capital && (styled || standalone) then LineKind.Heading level
            elif words <= 14 && text.Length <= 100 && styled && not endsLikeSentence && not hasDecimals && capital
                 && (numbered || ((bold || big) && standalone && flushLeft && words >= 2)) && proseWordCount l >= 1 then
                LineKind.Heading level
            elif l.Size < body * 0.85 then LineKind.Drop "small text"
            else
                // table rows and plot ticks: mostly bare numbers, few words, no math
                let numericWords =
                    l.Words |> Array.filter (fun w ->
                        not (w.Glyphs |> Array.exists (fun g -> g.IsMath))
                        && w.Text |> Seq.forall (fun ch -> not (Char.IsLetter ch)))
                let fam = gs |> Array.countBy (fun g -> fontFamily g.Font) |> Array.maxBy snd |> fst
                let rare = familyShare.[fam] < 0.015 && not (MathText.isItalicFont fam) && not bold
                if l.Words.Length >= 3 && float numericWords.Length >= 0.5 * float l.Words.Length && proseWordCount l <= 2 then LineKind.Drop "table/figure"
                elif rare && l.Words.Length <= 6 then LineKind.Drop "figure text"
                else LineKind.Text

    let classified = lines |> Array.map (fun l -> { Line = l; Kind = classify l })
    // A math-heavy row sitting between rows of a display equation (e.g. the main row of a tall fraction
    // that starts at the margin) belongs to that equation.
    for _ in 1 .. 2 do
        for k in 0 .. classified.Length - 1 do
            let c = classified.[k]
            if c.Kind = LineKind.Text then
                let gs = nonSpace c.Line
                let math = gs |> Array.filter (fun g -> g.IsMath) |> Array.length
                if proseWordCount c.Line <= 1 && float math >= 0.35 * float gs.Length then
                    let nearDisplay (j: int) =
                        j >= 0 && j < classified.Length && classified.[j].Kind = LineKind.Display
                        && classified.[j].Line.Page = c.Line.Page && classified.[j].Line.Region = c.Line.Region
                        && abs (classified.[j].Line.Baseline - c.Line.Baseline) <= body * 1.6
                    if nearDisplay (k - 1) || nearDisplay (k + 1) then classified.[k] <- { c with Kind = LineKind.Display }
    trace |> Option.iter (fun t ->
        for c in classified do
            t (sprintf "p%d %-5A %-22s size=%.1f base=%.0f | %s" (c.Line.Page + 1) c.Line.Region (sprintf "%A" c.Kind) c.Line.Size c.Line.Baseline c.Line.Text))

    // --- title: largest text on the first page, top half
    let firstPage = classified |> Array.filter (fun c -> c.Line.Page = 0)
    let titleLines =
        let crop, _ = cropOf.[0]
        let candidates =
            firstPage |> Array.filter (fun c -> c.Line.Top > crop.Top - crop.Height * 0.5 && (match c.Kind with LineKind.Drop _ -> false | _ -> true))
        if candidates.Length = 0 then [||]
        else
            let maxSize = candidates |> Array.map (fun c -> c.Line.Size) |> Array.max
            if maxSize < body * 1.2 then [||]
            else candidates |> Array.filter (fun c -> c.Line.Size >= maxSize * 0.92) |> Array.truncate 4
    let metaTitle =
        try
            let t = doc.Information.Title
            if String.IsNullOrWhiteSpace t || t.StartsWith "Microsoft Word" || t.ToLowerInvariant().Contains "untitled" then None
            else Some(t.Trim())
        with _ -> None
    let title =
        if titleLines.Length > 0 then titleLines |> Array.map (fun c -> c.Line.Text) |> String.concat " " |> MathText.tidy
        else defaultArg metaTitle (IO.Path.GetFileNameWithoutExtension path)
    let titleSet = Collections.Generic.HashSet<Line>(titleLines |> Seq.map (fun c -> c.Line), HashIdentity.Reference)

    // Front matter (authors, affiliations) between the title and the abstract is skipped.
    let abstractIndex =
        classified
        |> Array.tryFindIndex (fun c ->
            c.Line.Page <= 1
            && (let t = c.Line.Text.TrimStart().ToLowerInvariant()
                t.StartsWith "abstract"))
    let firstBodyIndex = defaultArg abstractIndex 0
    let isFrontMatter (l: Line) =
        match abstractIndex with
        | Some k ->
            let a = classified.[k].Line
            l.Page = a.Page && l.Baseline > a.Baseline + body * 1.5
        | None -> false

    // --- assemble units
    let units = ResizeArray<SourceUnit>()
    let visuals = ResizeArray<Visual>()
    let sections = ResizeArray<string>([ title ])
    let mutable section = 0
    let mutable counter = 0
    let nextId prefix =
        counter <- counter + 1
        sprintf "%s%d" prefix counter

    let toPageRectPadded (padX: float) (padY: float) page (l: float, r: float, b: float, t: float) =
        let (crop: UglyToad.PdfPig.Core.PdfRectangle), rot = cropOf.[page]
        if rot <> 0 then None
        else
            let x0 = max crop.Left (l - padX)
            let x1 = min crop.Right (r + padX)
            let y0 = min crop.Top (t + padY)
            let y1 = max crop.Bottom (b - padY)
            Some { Page = page; X = x0 - crop.Left; Y = crop.Top - y0; W = x1 - x0; H = y0 - y1 }
    let toPageRect = toPageRectPadded (body * 0.45) (body * 0.45)

    units.Add
        { Id = "T0"; Kind = UnitKind.Title; Text = title; Spoken = SpeechText.forSpeech title
          Visual = None; Page = 0; Section = 0; ParagraphEnd = true }

    // paragraph state
    let para = ResizeArray<Placed>()

    let emitSentence (words: Placed list) (paragraphEnd: bool) =
        if not words.IsEmpty then
            let rendered = words |> List.map (fun p -> renderWord p.Line p.Word)
            let text = rendered |> List.map fst |> String.concat " " |> MathText.tidy
            let spoken = rendered |> List.map snd |> String.concat " " |> SpeechText.forSpeech
            let id = nextId "S"
            // Inline math: cut out just the formula fragments of the sentence (tightly, so they show large),
            // up to three of them, stacked into one image.
            let isMathWord (w: Word) = w.Glyphs |> Array.exists (fun g -> g.IsMath && not (".,;:()".Contains g.Value))
            let clusters =
                words
                |> List.groupBy (fun p -> p.Line)
                |> List.collect (fun (line, ps) ->
                    let ws = ps |> List.map (fun p -> p.Word) |> List.sortBy (fun w -> w.Left) |> Array.ofList
                    let runs = ResizeArray<Word list>()
                    let mutable current: Word list = []
                    let mutable gap = 0
                    for w in ws do
                        if isMathWord w then
                            current <- current @ [ w ]
                            gap <- 0
                        elif not current.IsEmpty && gap = 0 && w.Text.Length <= 3 then
                            // a short connector ("and", "=", ",") may sit between two math words
                            current <- current @ [ w ]
                            gap <- 1
                        else
                            if not current.IsEmpty then runs.Add current
                            current <- []
                            gap <- 0
                    if not current.IsEmpty then runs.Add current
                    [ for run in runs do
                          // drop a trailing connector word ("and"), but keep closing brackets and numbers
                          let last = List.last run
                          let run = if isMathWord last || not (last.Text |> Seq.exists Char.IsLetter) then run else run |> List.take (run.Length - 1)
                          let glyphs = run |> List.collect (fun w -> List.ofArray w.Glyphs)
                          let math = glyphs |> List.filter (fun g -> g.IsMath) |> List.length
                          let scripted = glyphs |> List.exists (fun g -> g.Size < line.Size * 0.85)
                          if math >= 2 || scripted then
                              let l = glyphs |> List.map (fun g -> min g.Left g.StartX) |> List.min
                              let r = glyphs |> List.map (fun g -> max g.Right g.EndX) |> List.max
                              // glyph boxes can be too small for tight crops: use ascender/descender from the font size
                              let b = min (line.Baseline - 0.3 * line.Size) (glyphs |> List.map (fun g -> min g.Bottom (g.Baseline - 0.25 * g.Size)) |> List.min)
                              let t = max (line.Baseline + 0.8 * line.Size) (glyphs |> List.map (fun g -> max g.Top (g.Baseline + 0.8 * g.Size)) |> List.max)
                              yield line, math, (l, r, b, t) ])
            let visual =
                if clusters.IsEmpty then None
                else
                    let (bestLine, _, _) = clusters |> List.maxBy (fun (_, m, _) -> m)
                    let chosen =
                        clusters
                        |> List.filter (fun (l, _, _) -> l.Page = bestLine.Page)
                        |> List.indexed
                        |> List.sortByDescending (fun (_, (_, m, _)) -> m)
                        |> List.truncate 3
                        |> List.sortBy fst
                        |> List.choose (fun (_, (l, _, box)) -> toPageRectPadded (body * 0.22) (body * 0.08) l.Page box)
                    if chosen.IsEmpty then None
                    else
                        let v = { Id = id; Kind = VisualKind.Inline; Page = chosen.Head.Page; Parts = Array.ofList chosen; EqNumber = None; RawText = text; Latex = None }
                        visuals.Add v
                        Some v.Id
            if spoken.Length > 0 && (spoken |> Seq.exists Char.IsLetterOrDigit) then
                units.Add
                    { Id = id; Kind = UnitKind.Sentence; Text = text; Spoken = spoken; Visual = visual
                      Page = (List.head words).Line.Page; Section = section; ParagraphEnd = paragraphEnd }

    let flushBody (paragraphEnd: bool) =
        if para.Count > 0 then
            // join hyphenated line breaks
            let merged = ResizeArray<Placed>()
            for p in para do
                if merged.Count > 0 then
                    let prev = merged.[merged.Count - 1]
                    let pt = prev.Word.Text
                    let nt = p.Word.Text
                    let compound = Char.IsUpper pt.[0] || nt.Contains "-"
                    if prev.Line <> p.Line && pt.Length > 2 && (pt.EndsWith "-" || pt.EndsWith "‐")
                       && Char.IsLetter pt.[pt.Length - 2] && nt.Length > 0 && Char.IsLower nt.[0] && not compound then
                        let glyphs = Array.append prev.Word.Glyphs.[.. prev.Word.Glyphs.Length - 2] p.Word.Glyphs
                        // keep the glyph geometry of both parts but order text by reading, not by x
                        let w = { prev.Word with Glyphs = glyphs; Right = max prev.Word.Right p.Word.Right }
                        merged.[merged.Count - 1] <- { prev with Word = w }
                    elif prev.Line <> p.Line && pt.Length > 2 && pt.EndsWith "-" && nt.Length > 0 && Char.IsLetter nt.[0] then
                        // a compound split at the hyphen: glue the parts, keeping the hyphen
                        let w = { prev.Word with Glyphs = Array.append prev.Word.Glyphs p.Word.Glyphs; Right = max prev.Word.Right p.Word.Right }
                        merged.[merged.Count - 1] <- { prev with Word = w }
                    else merged.Add p
                else merged.Add p
            // split into sentences
            let current = ResizeArray<Placed>()
            for i in 0 .. merged.Count - 1 do
                current.Add merged.[i]
                let next = if i + 1 < merged.Count then merged.[i + 1].Word.Text else ""
                let tooLong = current.Count >= 70 && (merged.[i].Word.Text.EndsWith ";" || merged.[i].Word.Text.EndsWith ",")
                // "Figure 1." / "Table 2." opening a caption is a label, not a sentence
                let label = current.Count = 2 && captionWordRx.IsMatch current.[0].Word.Text
                if not label && ((next <> "" && SpeechText.endsSentence merged.[i].Word.Text next) || tooLong) then
                    emitSentence (List.ofSeq current) false
                    current.Clear()
            emitSentence (List.ofSeq current) paragraphEnd
            para.Clear()

    // Captions interrupt a column's text wherever the figure sits. They are collected separately so the
    // paragraph around them stays whole, and read after it.
    let captions = ResizeArray<ResizeArray<Placed>>()
    let flushParagraph (paragraphEnd: bool) =
        flushBody paragraphEnd
        for c in captions do
            para.AddRange c
            flushBody true
        captions.Clear()

    let textRight =
        classified
        |> Array.filter (fun c -> c.Kind = LineKind.Text)
        |> Array.groupBy (fun c -> c.Line.Page, c.Line.Region)
        |> Array.map (fun (k, cs) -> k, (cs |> Array.map (fun c -> c.Line.Right) |> Array.sort |> fun a -> a.[a.Length * 9 / 10]))
        |> dict
    let textLeft =
        classified
        |> Array.filter (fun c -> c.Kind = LineKind.Text)
        |> Array.groupBy (fun c -> c.Line.Page, c.Line.Region)
        |> Array.map (fun (k, cs) -> k, (cs |> Array.map (fun c -> c.Line.Left) |> Array.sort |> fun a -> a.[a.Length / 10]))
        |> dict

    let mutable prevText: Line option = None
    let mutable captionLine: Line option = None // the last line of the caption being collected
    let mutable skipping = false // inside the references section
    let mutable i = 0
    let n = classified.Length

    let emitHeading (text: string) (page: int) =
        section <- section + 1
        let clean = MathText.tidy text
        let clean =
            if upperFraction clean > 0.8 then
                let ti = Globalization.CultureInfo.InvariantCulture.TextInfo
                Regex.Replace(ti.ToTitleCase(clean.ToLowerInvariant()), @"(['’])S\b", "$1s")
            else clean
        sections.Add clean
        let m = Regex.Match(clean, @"^((\d{1,2}(\.\d{1,2})*|[A-Z](\.\d{1,2})*)\.?)\s+(.*)$")
        let spoken =
            if m.Success && m.Groups.[5].Value.Length > 0 then
                let num = m.Groups.[2].Value
                let word = if Char.IsLetter num.[0] && num.Length = 1 then "Appendix" else "Section"
                sprintf "%s %s. %s." word num m.Groups.[5].Value
            else clean.TrimEnd('.') + "."
        units.Add
            { Id = nextId "H"; Kind = UnitKind.Heading; Text = clean; Spoken = SpeechText.forSpeech spoken
              Visual = None; Page = page; Section = section; ParagraphEnd = true }

    while i < n do
        let c =
            // an "Abstract—We propose ..." line in body style is still the abstract heading
            if Some i = abstractIndex && classified.[i].Kind = LineKind.Text then { classified.[i] with Kind = LineKind.Heading 1 }
            else classified.[i]
        let l = c.Line
        if i < firstBodyIndex || titleSet.Contains l || isFrontMatter l then i <- i + 1
        else
            let algo = if skipping || c.Kind <> LineKind.Text then None else (let m = algorithmRx.Match(l.Text) in if m.Success then Some m.Groups.[1].Value else None)
            match c.Kind with
            | LineKind.Text when algo.IsSome ->
                // An algorithm listing: shown as one image; the caption is read, the model walks through the steps.
                flushParagraph true
                let block = ResizeArray<ClassifiedLine>([ c ])
                let mutable j = i + 1
                let keep (k: LineKind) = match k with LineKind.Heading _ | LineKind.Drop "header/footer" | LineKind.Drop "footnote" -> false | _ -> true
                while j < n
                      && classified.[j].Line.Page = l.Page && classified.[j].Line.Region = l.Region
                      && keep classified.[j].Kind
                      && block.[block.Count - 1].Line.Baseline - classified.[j].Line.Baseline < body * 2.3 do
                    block.Add classified.[j]
                    j <- j + 1
                let lines = block |> Seq.map (fun b -> b.Line) |> Seq.toArray
                let caption =
                    lines |> Seq.takeWhile (fun x -> not (algorithmStepRx.IsMatch(x.Text.TrimStart()))) |> Seq.truncate 4
                    |> Seq.map (fun x -> x.Words |> Array.map (renderWord x >> snd) |> String.concat " ") |> String.concat " "
                let raw = lines |> Seq.map (fun x -> x.Words |> Array.map (renderWord x >> fst) |> String.concat " ") |> String.concat "\n"
                let glyphs = lines |> Array.collect (fun x -> x.Glyphs)
                let id = nextId "E"
                match toPageRect l.Page (bounds glyphs) with
                | Some rect ->
                    visuals.Add { Id = id; Kind = VisualKind.Algorithm; Page = rect.Page; Parts = [| rect |]; EqNumber = algo; RawText = raw; Latex = None }
                    units.Add
                        { Id = id; Kind = UnitKind.Equation; Text = raw
                          Spoken = SpeechText.forSpeech (MathText.tidy caption + " The algorithm is shown on screen.")
                          Visual = Some id; Page = l.Page; Section = section; ParagraphEnd = true }
                | None -> ()
                prevText <- None
                i <- j
            | LineKind.Drop _ -> i <- i + 1
            | LineKind.Heading _ ->
                let text = l.Text.Trim()
                if referencesRx.IsMatch(text.TrimEnd('.', ':')) then
                    flushParagraph true
                    skipping <- true
                elif skipping && appendixRx.IsMatch text then
                    skipping <- false
                    emitHeading text l.Page
                elif not skipping then
                    flushParagraph true
                    // "Abstract—We propose ..." style: heading word followed by text on the same line
                    let t = text.ToLowerInvariant()
                    if t.StartsWith "abstract" && text.Length > 12 then
                        emitHeading "Abstract" l.Page
                        // drop the word "Abstract" and any dash/colon glued to it
                        let first = l.Words.[0]
                        let tail =
                            first.Glyphs |> Array.skip (min first.Glyphs.Length 8)
                            |> Array.skipWhile (fun g -> "—–-.: ".Contains g.Value)
                        if tail.Length > 0 then para.Add { Word = mkWord tail; Line = l }
                        for w in l.Words |> Array.skip 1 do para.Add { Word = w; Line = l }
                        prevText <- Some l
                    else
                        emitHeading text l.Page
                        prevText <- None
                i <- i + 1
            | _ when skipping -> i <- i + 1
            | LineKind.Display
            | LineKind.Fragment when
                (c.Kind = LineKind.Display
                 || (i + 1 < n && classified.[i + 1].Kind = LineKind.Display && classified.[i + 1].Line.Page = l.Page)) ->
                // gather the whole display block: consecutive display lines and stray fragments nearby
                let group = ResizeArray<Line>([ l ])
                let mutable j = i + 1
                let near (a: Line) (b: Line) = a.Page = b.Page && a.Region = b.Region && abs (a.Baseline - b.Baseline) < body * 4.2
                while j < n
                      && (classified.[j].Kind = LineKind.Display || classified.[j].Kind = LineKind.Fragment)
                      && near group.[group.Count - 1] classified.[j].Line do
                    group.Add classified.[j].Line
                    j <- j + 1
                // Pure-fragment groups ("2", "t") are leftovers, not equations.
                if group |> Seq.exists (fun g -> isDisplayMath g) then
                    // a paragraph interrupted by an equation: speak what came before first
                    flushParagraph false
                    let glyphs = group |> Seq.collect (fun g -> g.Glyphs) |> Seq.toArray
                    let bl, br, bb, bt = bounds glyphs
                    let numbers =
                        group |> Seq.collect (fun g -> g.Words) |> Seq.filter (fun w -> isEqNumber w.Text && w.Left > (bl + br) / 2.0)
                        |> Seq.map (fun w -> w.Text.Trim('.', ',').Trim('(', ')')) |> Seq.toList
                    let eqNumber =
                        match numbers with
                        | [] -> None
                        | [ a ] -> Some a
                        | a :: rest -> Some(a + "–" + List.last rest)
                    let raw = group |> Seq.map (fun g -> g.Words |> Array.map (renderWord g >> fst) |> String.concat " ") |> String.concat "\n"
                    let spokenEq = group |> Seq.map (fun g -> g.Words |> Array.map (renderWord g >> snd) |> String.concat " ") |> String.concat " "
                    let id = nextId "E"
                    match toPageRect l.Page (bl, br, bb, bt) with
                    | Some rect ->
                        visuals.Add { Id = id; Kind = VisualKind.Equation; Page = rect.Page; Parts = [| rect |]; EqNumber = eqNumber; RawText = raw; Latex = None }
                        let name = match eqNumber with Some e when e.Contains "–" -> sprintf "Equations %s" e | Some e -> sprintf "Equation %s" e | None -> "An equation"
                        let spoken =
                            let s = MathText.tidy spokenEq
                            if group.Count = 1 && glyphs.Length <= 30 && s.Length > 0 then
                                let withoutNumber = Regex.Replace(s, @"\(\s*[\w.]+\s*\)\s*$", "")
                                sprintf "%s: %s." (if eqNumber.IsSome then name else "In symbols") withoutNumber
                            else sprintf "%s %s on screen." name (if name.StartsWith "Equations" then "are" else "is")
                        units.Add
                            { Id = id; Kind = UnitKind.Equation; Text = raw; Spoken = SpeechText.forSpeech spoken
                              Visual = Some id; Page = l.Page; Section = section; ParagraphEnd = false }
                    | None -> ()
                    prevText <- None
                i <- j
            | LineKind.Fragment -> i <- i + 1
            | _ when captionStartRx.IsMatch l.Text ->
                captions.Add(ResizeArray(l.Words |> Seq.map (fun w -> { Word = w; Line = l })))
                captionLine <- Some l
                i <- i + 1
            | _ when (match captionLine with
                      | Some c ->
                          c.Page = l.Page && c.Region = l.Region && abs (c.Size - l.Size) < 0.3
                          && c.Baseline - l.Baseline > 0.0 && c.Baseline - l.Baseline < body * 1.9
                      | None -> false) ->
                // the caption continues: same size, right below
                captions.[captions.Count - 1].AddRange(l.Words |> Seq.map (fun w -> { Word = w; Line = l }))
                captionLine <- Some l
                i <- i + 1
            | _ ->
                captionLine <- None
                // text line: decide whether it starts a new paragraph
                match prevText with
                | Some p ->
                    let sameCol = p.Page = l.Page && p.Region = l.Region
                    let pTrim = p.Text.TrimEnd()
                    let endsSentence = pTrim.EndsWith "." || pTrim.EndsWith ":" || pTrim.EndsWith "?" || pTrim.EndsWith "!"
                    let right = match textRight.TryGetValue((p.Page, p.Region)) with | true, r -> r | _ -> p.Right
                    let left = match textLeft.TryGetValue((l.Page, l.Region)) with | true, x -> x | _ -> l.Left
                    let shortLine = p.Right < right - body * 1.5
                    let indented = l.Left > left + body * 0.8
                    let bigGap = sameCol && (p.Baseline - l.Baseline) > body * 1.9
                    if bigGap || (endsSentence && (shortLine || indented)) then flushParagraph true
                | None -> ()
                for w in l.Words do para.Add { Word = w; Line = l }
                prevText <- Some l
                i <- i + 1
    flushParagraph true

    { Title = title
      Source = "a PDF (most likely a research paper), text extracted from the page layout"
      PageCount = pageCount
      Sections = sections.ToArray()
      Units = units.ToArray()
      Visuals = visuals.ToArray() }
