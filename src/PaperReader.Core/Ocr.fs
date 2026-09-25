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
