/// Uses Mistral OCR output to check the equation crops and to read the equations as LaTeX.
module PaperReader.Core.Ocr

open System
open System.Text.RegularExpressions

let private tagRx = Regex(@"\\tag\*?\{[^}]*\}", RegexOptions.Compiled)
let private mathRx = Regex(@"\$\$(.+?)\$\$|\\\[(.+?)\\\]|\\\((.+?)\\\)|\$(.+?)\$", RegexOptions.Compiled ||| RegexOptions.Singleline)

/// The formula without display delimiters or an equation-number tag.
let clean (latex: string) =
    let s = latex.Trim()
    let s =
        if s.StartsWith "$$" && s.EndsWith "$$" && s.Length >= 4 then s.Substring(2, s.Length - 4)
        elif s.StartsWith @"\[" && s.EndsWith @"\]" then s.Substring(2, s.Length - 4)
        elif s.StartsWith @"\(" && s.EndsWith @"\)" then s.Substring(2, s.Length - 4)
        elif s.StartsWith "$" && s.EndsWith "$" && s.Length >= 2 then s.Substring(1, s.Length - 2)
        else s
    (tagRx.Replace(s, "")).Trim()

/// The math pieces of OCR markdown ($$…$$, \[…\], \(…\), $…$), cleaned.
let mathPieces (markdown: string) : string list =
    [ for m in mathRx.Matches markdown do
          let g = [ 1 .. 4 ] |> List.map (fun k -> m.Groups.[k]) |> List.find (fun g -> g.Success)
          let piece = clean g.Value
          if piece <> "" then yield piece ]

/// Letters and digits, lowercased: what OCR LaTeX and PDF-extracted text have in common.
let private signature (s: string) =
    let s = Regex.Replace(s, @"\\[a-zA-Z]+", " ") // LaTeX commands carry no glyphs of their own
    s |> Seq.filter Char.IsLetterOrDigit |> Seq.map Char.ToLowerInvariant |> Array.ofSeq

/// Share of characters two texts have in common (as multisets), 0..1.
let similarity (a: string) (b: string) =
    let sa, sb = signature a, signature b
    if sa.Length = 0 || sb.Length = 0 then 0.0
    else
        let counts = sb |> Array.countBy id |> dict |> Collections.Generic.Dictionary
        let mutable common = 0
        for c in sa do
            match counts.TryGetValue c with
            | true, n when n > 0 ->
                counts.[c] <- n - 1
                common <- common + 1
            | _ -> ()
        float common / float (max sa.Length sb.Length)

/// The math pieces that best match a visual's extracted text, in reading order.
let bestPieces (raw: string) (pieces: string list) : string list =
    match pieces with
    | [] -> []
    | [ p ] -> [ p ]
    | _ ->
        let scored = pieces |> List.map (fun p -> p, similarity raw p)
        let best = scored |> List.map snd |> List.max
        scored |> List.filter (fun (_, s) -> s >= best * 0.6 && s > 0.15) |> List.map fst

let private area (r: PageRect) = max 0.0 r.W * max 0.0 r.H

let private intersection (a: PageRect) (b: PageRect) =
    let x0, y0 = max a.X b.X, max a.Y b.Y
    let x1, y1 = min (a.X + a.W) (b.X + b.W), min (a.Y + a.H) (b.Y + b.H)
    max 0.0 (x1 - x0) * max 0.0 (y1 - y0)

/// How far OCR may move an edge of a crop, in points (guards against a block that swallowed its neighbours).
let maxGrowth = 40.0

/// Grows display equations to the full extent Mistral OCR found for them (so no part is cut off) and
/// records their LaTeX. `sizes` are the pages' crop boxes in points.
let refine (pages: Mistral.OcrPage list) (sizes: (float * float)[]) (visuals: Visual[]) : Visual[] =
    let byIndex = pages |> List.map (fun p -> p.Index, p) |> dict
    visuals
    |> Array.map (fun v ->
        match v.Kind with
        | VisualKind.Equation when v.Page < sizes.Length && byIndex.ContainsKey v.Page ->
            let page = byIndex.[v.Page]
            let pw, ph = sizes.[v.Page]
            if page.Width <= 0.0 || page.Height <= 0.0 then v
            else
                let sx, sy = pw / page.Width, ph / page.Height
                let blocks =
                    page.Blocks
                    |> List.filter (fun b -> b.Kind = "equation")
                    |> List.map (fun b ->
                        { Page = v.Page; X = b.X0 * sx; Y = b.Y0 * sy; W = (b.X1 - b.X0) * sx; H = (b.Y1 - b.Y0) * sy }, b.Content)
                let matched = Collections.Generic.List<string>()
                let parts =
                    v.Parts
                    |> Array.map (fun part ->
                        let hits =
                            blocks
                            |> List.filter (fun (r, _) ->
                                let i = intersection part r
                                i > 0.0 && i >= 0.3 * min (area part) (area r))
                        for (_, content) in hits do
                            if not (matched.Contains content) then matched.Add content
                        hits
                        |> List.fold (fun (p: PageRect) (r, _) ->
                            let x0 = max 0.0 (max (p.X - maxGrowth) (min p.X r.X))
                            let y0 = max 0.0 (max (p.Y - maxGrowth) (min p.Y r.Y))
                            let x1 = min pw (min (p.X + p.W + maxGrowth) (max (p.X + p.W) (r.X + r.W)))
                            let y1 = min ph (min (p.Y + p.H + maxGrowth) (max (p.Y + p.H) (r.Y + r.H)))
                            { p with X = x0; Y = y0; W = x1 - x0; H = y1 - y0 }) part)
                let latex =
                    matched |> Seq.collect (fun c -> match mathPieces c with [] -> [ clean c ] | ps -> ps) |> Seq.filter ((<>) "") |> List.ofSeq
                { v with Parts = parts; Latex = (if latex.IsEmpty then v.Latex else Some(String.Join("\n", latex))) }
        | _ -> v)

// ---- figures and tables

let private captionRx =
    Regex(@"^\W*(Figure|Fig\.?|Table)\s*([A-Z]?\d+[a-z]?)\s*[:.|]", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// "Figure 3: …" / "Fig. 3." / "Table 2:" at the start of a caption: the kind and number.
let captionOf (text: string) : (VisualKind * string) option =
    let m = captionRx.Match text
    if not m.Success then None
    else
        let kind = if m.Groups.[1].Value.StartsWith("T", StringComparison.OrdinalIgnoreCase) then VisualKind.Table else VisualKind.Figure
        Some(kind, m.Groups.[2].Value)

/// Id of a figure or table visual, e.g. "Fig3", "Tab2".
let figureId (kind: VisualKind) (number: string) =
    (if kind = VisualKind.Table then "Tab" else "Fig") + number

let private union (rs: PageRect list) =
    let x0 = rs |> List.map (fun r -> r.X) |> List.min
    let y0 = rs |> List.map (fun r -> r.Y) |> List.min
    let x1 = rs |> List.map (fun r -> r.X + r.W) |> List.max
    let y1 = rs |> List.map (fun r -> r.Y + r.H) |> List.max
    { rs.Head with X = x0; Y = y0; W = x1 - x0; H = y1 - y0 }

/// Figures and tables with their captions, from OCR's image, table and caption blocks. A figure is the
/// images between its caption and the caption (or page top) above it, with any sub-captions among them;
/// a table is the table block nearest its caption. The region includes the caption.
let figures (pages: Mistral.OcrPage list) (sizes: (float * float)[]) : Visual list =
    let seen = Collections.Generic.HashSet<string>()
    [ for page in pages |> List.sortBy (fun p -> p.Index) do
          if page.Index < sizes.Length && page.Width > 0.0 && page.Height > 0.0 then
              let pw, ph = sizes.[page.Index]
              let sx, sy = pw / page.Width, ph / page.Height
              let rect (b: Mistral.OcrBlock) = { Page = page.Index; X = b.X0 * sx; Y = b.Y0 * sy; W = (b.X1 - b.X0) * sx; H = (b.Y1 - b.Y0) * sy }
              let blocks = page.Blocks |> List.map (fun b -> b, rect b)
              let captions =
                  blocks
                  |> List.choose (fun (b, r) -> if b.Kind = "caption" then captionOf b.Content |> Option.map (fun (k, n) -> b, r, k, n) else None)
                  |> List.sortBy (fun (_, r, _, _) -> r.Y)
              let claimed = Collections.Generic.HashSet<Mistral.OcrBlock>(HashIdentity.Reference)
              for (i, (cb, cr, kind, number)) in List.indexed captions do
                  let above = captions |> List.take i |> List.map (fun (_, r, _, _) -> r.Y + r.H) |> List.fold max 0.0
                  let content =
                      match kind with
                      | VisualKind.Table ->
                          blocks
                          |> List.filter (fun (b, _) -> b.Kind = "table" && not (claimed.Contains b))
                          |> List.sortBy (fun (_, r) -> min (abs (r.Y - (cr.Y + cr.H))) (abs (cr.Y - (r.Y + r.H))))
                          |> List.truncate 1
                      | _ ->
                          let images =
                              blocks
                              |> List.filter (fun (b, r) ->
                                  b.Kind = "image" && not (claimed.Contains b) && r.Y + r.H / 2.0 >= above && r.Y + r.H / 2.0 <= cr.Y + 2.0)
                          match images with
                          | [] -> []
                          | _ ->
                              let top = images |> List.map (fun (_, r) -> r.Y) |> List.min
                              // sub-captions ("(a) …", panel titles) between the images and the caption
                              let subCaptions =
                                  blocks
                                  |> List.filter (fun (b, r) ->
                                      b.Kind = "caption" && not (obj.ReferenceEquals(b, cb)) && (captionOf b.Content).IsNone
                                      && r.Y >= top - 20.0 && r.Y + r.H <= cr.Y + 2.0)
                              images @ subCaptions
                  if not content.IsEmpty then
                      for (b, _) in content do claimed.Add b |> ignore
                      let id = figureId kind number
                      if seen.Add id then
                          let r = union (cr :: (content |> List.map snd))
                          let pad = 4.0
                          let x0, y0 = max 0.0 (r.X - pad), max 0.0 (r.Y - pad)
                          let x1, y1 = min pw (r.X + r.W + pad), min ph (r.Y + r.H + pad)
                          yield
                              { Id = id
                                Kind = kind
                                Parts = [| { r with X = x0; Y = y0; W = x1 - x0; H = y1 - y0 } |]
                                EqNumber = Some number
                                RawText = cb.Content
                                Latex = None } ]

/// Points each figure's or table's caption sentence at its image.
let linkCaptions (figures: Visual list) (units: SourceUnit[]) =
    let pending = figures |> List.map (fun v -> v.Id, v.Page) |> dict |> Collections.Generic.Dictionary
    units
    |> Array.map (fun u ->
        match u.Kind, captionOf u.Text with
        | UnitKind.Sentence, Some (kind, number) ->
            let id = figureId kind number
            match pending.TryGetValue id with
            | true, page when abs (page - u.Page) <= 1 ->
                pending.Remove id |> ignore
                { u with Visual = Some id }
            | _ -> u
        | _ -> u)
