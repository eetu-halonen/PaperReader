/// The equation images: page regions cut out of the PDF, cleaned up, and checked to be whole.
module PaperReader.Crops

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open SkiaSharp
open CSharpMath.SkiaSharp
open PaperReader.Core

let private gapPx = 24
/// How far an edge moves per step while a glyph is cut off there, and at most, in points. A glyph is
/// narrower than the limit; ink that still reaches the edge after it is running text next to the formula.
let private stepPt = 1.0
let private maxGrowthPt = 8.0

/// Renders one region, moving each edge out one point at a time while ink is cut off there, until every
/// glyph is whole. An edge that reaches the limit goes back where it was (the neighbouring text stays cut,
/// as before). Returns the tidied pixels, whether a glyph is still cut, and whether the crop is empty.
let private renderPart (pdf: IPdfPages) (pageSize: float * float) (region: PageRect) (scale: float) (dropNumber: bool) =
    let pw, ph = pageSize
    // edges as distances grown: left, top, right, bottom; None = given up on that side
    let grown = [| Some 0.0; Some 0.0; Some 0.0; Some 0.0 |]
    let rectOf () =
        let g k = defaultArg grown.[k] 0.0
        let x0, y0 = max 0.0 (region.X - g 0), max 0.0 (region.Y - g 1)
        let x1, y1 = min pw (region.X + region.W + g 2), min ph (region.Y + region.H + g 3)
        { region with X = x0; Y = y0; W = x1 - x0; H = y1 - y0 }
    task {
        let mutable result = None
        while result.IsNone do
            let r = rectOf ()
            let! px, w, h = pdf.Render(r.Page, r, scale)
            CropTidy.clean px w h dropNumber
            let l, t, rt, b = CropTidy.clipped px w h
            // an edge on the page border can't be cut off
            let cut = [| l && r.X > 0.0; t && r.Y > 0.0; rt && r.X + r.W < pw; b && r.Y + r.H < ph |]
            let mutable moved = false
            for k in 0 .. 3 do
                match grown.[k] with
                | Some g when cut.[k] ->
                    if g + stepPt > maxGrowthPt then grown.[k] <- None
                    else
                        grown.[k] <- Some(g + stepPt)
                        moved <- true
                | _ -> ()
            if not moved && rectOf () = r then
                let px, w, h = CropTidy.trim px w h
                let stillCut = cut |> Array.indexed |> Array.exists (fun (k, c) -> c && grown.[k].IsSome)
                result <- Some((px, w, h), stillCut, CropTidy.blank px)
        return result.Value
    }

let private toBitmap (px: int[], w: int, h: int) =
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

let private encode (bmp: SKBitmap) =
    use image = SKImage.FromBitmap bmp
    use data = image.Encode(SKEncodedImageFormat.Png, 100)
    data.ToArray()

/// Parts stacked top to bottom, centred, with a thin divider between them.
let private stack (parts: SKBitmap list) =
    let w = parts |> List.map (fun b -> b.Width) |> List.max
    let h = (parts |> List.sumBy (fun b -> b.Height)) + gapPx * (parts.Length - 1)
    let bmp = new SKBitmap(w, h)
    use canvas = new SKCanvas(bmp)
    canvas.Clear SKColors.White
    use divider = new SKPaint(Color = SKColor(224uy, 226uy, 230uy), StrokeWidth = 3.0f)
    let mutable top = 0
    parts |> List.iteri (fun k part ->
        if k > 0 then
            let y = float32 (top - gapPx / 2)
            canvas.DrawLine(0.0f, y, float32 w, y, divider)
        canvas.DrawBitmap(part, float32 ((w - part.Width) / 2), float32 top)
        top <- top + part.Height + gapPx)
    bmp

/// Typesets LaTeX (one formula per line) at about the size of a crop of 10 pt text. None if it can't be parsed.
let renderLatex (latex: string) : SKBitmap option =
    let lines = latex.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    let rendered =
        lines |> Array.map (fun line ->
            // crops are 3 px per point, so 10 pt text has a 30 px em
            let painter = MathPainter(FontSize = 30.0f, TextColor = SKColors.Black)
            painter.LaTeX <- line
            if not (isNull painter.ErrorMessage) then None
            else
                let r = painter.Measure(0.0f)
                // the measured width leaves out the overhang of a final italic letter
                let pad, extra = 10.0f, 8.0f
                let w, h = int (ceil (r.Width + 2.0f * pad + extra)), int (ceil (r.Height + 2.0f * pad))
                if w <= 0 || h <= 0 then None
                else
                    let bmp = new SKBitmap(w, h)
                    use c = new SKCanvas(bmp)
                    c.Clear SKColors.White
                    painter.Draw(c, pad - r.X, pad - r.Y)
                    Some bmp)
    if lines.Length = 0 || rendered |> Array.exists Option.isNone then
        for b in rendered |> Array.choose id do b.Dispose()
        None
    elif rendered.Length = 1 then rendered.[0]
    else
        let parts = rendered |> Array.choose id |> List.ofArray
        let bmp = stack parts
        for b in parts do b.Dispose()
        Some bmp

/// What happened to one image, for the import summary.
type Outcome =
    | Whole
    /// Cut off or empty even after growing, and redrawn from the OCR'd LaTeX.
    | Typeset
    /// Cut off or empty, and no LaTeX to fall back on.
    | Imperfect

/// Renders every visual's image. A region is grown until no glyph is cut off; when that fails (or the
/// crop is empty) the formula is typeset from LaTeX instead: the OCR'd LaTeX of a display equation, or
/// `ocrImage` run on the grown crop. Existing files are kept.
let render (pdf: IPdfPages) (sizes: (float * float)[]) (crops: (Visual * string) list)
           (ocrImage: (Visual -> byte[] -> Task<string option>) option)
           (progress: int -> unit) (ct: CancellationToken) : Task<(string * Outcome) list> =
    task {
        let outcomes = Collections.Generic.List<string * Outcome>()
        let mutable doneCount = 0
        for (v, output) in crops do
            ct.ThrowIfCancellationRequested()
            if not (File.Exists output) && v.Page < sizes.Length then
                let widest = v.Parts |> Array.map (fun r -> r.W) |> Array.max
                let scale = min 3.0 (2400.0 / max 1.0 widest)
                let dropNumber = v.Kind = VisualKind.Equation && v.EqNumber.IsSome
                let results = Collections.Generic.List<(int[] * int * int) * bool * bool>()
                for r in v.Parts do
                    match v.Kind with
                    | VisualKind.Figure | VisualKind.Table ->
                        // the region comes from OCR and includes the caption: draw it as it is
                        let! px, w, h = pdf.Render(r.Page, r, scale)
                        let px, w, h = CropTidy.trim px w h
                        results.Add(((px, w, h), false, CropTidy.blank px))
                    | _ ->
                        let! part = renderPart pdf sizes.[r.Page] r scale dropNumber
                        results.Add part
                let results = results.ToArray()
                let parts = results |> Array.map (fun (p, _, _) -> toBitmap p) |> List.ofArray
                let cropped = stack parts
                for b in parts do b.Dispose()
                // typeset instead only when the crop can't be trusted: nothing on it, or a display equation
                // still cut off. OCR of a small inline crop can misread text next to it, so that is only
                // used for an empty crop.
                let empty = results |> Array.forall (fun (_, _, e) -> e)
                let cut = results |> Array.exists (fun (_, c, _) -> c)
                let broken = empty || (cut && v.Kind = VisualKind.Equation)
                let! image, outcome =
                    task {
                        if not broken then return cropped, Whole
                        else
                            let! latex =
                                match v.Latex, ocrImage with
                                | Some l, _ -> Task.FromResult(Some l)
                                | None, Some ocr when not empty -> ocr v (encode cropped)
                                | _ -> Task.FromResult None
                            match latex |> Option.bind renderLatex with
                            | Some typeset ->
                                cropped.Dispose()
                                return typeset, Typeset
                            | None -> return cropped, Imperfect
                    }
                use image = image
                let tmp = output + ".tmp"
                File.WriteAllBytes(tmp, encode image)
                File.Move(tmp, output, true)
                outcomes.Add((v.Id, outcome))
            doneCount <- doneCount + 1
            progress doneCount
        return List.ofSeq outcomes
    }
