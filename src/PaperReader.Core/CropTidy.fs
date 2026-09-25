/// Cleans up a rendered crop of a PDF page region. Works on ARGB pixels so every platform can use it.
module PaperReader.Core.CropTidy

let private white = 0xFFFFFFFF |> int

/// Runs of consecutive true values, as (first, last) index pairs.
let private runs (flags: bool[]) =
    [ let mutable i = 0
      while i < flags.Length do
          if flags.[i] then
              let s = i
              while i < flags.Length && flags.[i] do i <- i + 1
              yield s, i - 1
          else i <- i + 1 ]

/// Blanks slivers of the neighbouring lines cut by the top or bottom edge, drops a right-aligned
/// equation number, and trims the white margins. Returns the new pixels and size; `px` is modified.
let tidy (px: int[]) (w: int) (h: int) (dropNumber: bool) : int[] * int * int =
    let ink (c: int) =
        let r, g, b = (c >>> 16) &&& 0xff, (c >>> 8) &&& 0xff, c &&& 0xff
        r * 3 + g * 6 + b < 2300 // luminance below ~230, so faint antialiased slivers count too
    let rowInk y = Seq.exists (fun x -> ink px.[y * w + x]) (seq { 0 .. w - 1 })
    let blankRows a b = for y in a .. b do Array.fill px (y * w) w white
    // a band of rows cut by the edge belongs to the line above or below when it is thin, or when it
    // runs into the crop's sides (a centred numerator over a fraction bar does neither)
    let foreign (s, e) =
        let touchesSide =
            seq { s .. e } |> Seq.exists (fun y -> ink px.[y * w] || ink px.[y * w + 1] || ink px.[y * w + w - 1] || ink px.[y * w + w - 2])
        float (e - s + 1) < 0.22 * float h || touchesSide
    let rows = runs (Array.init h rowInk)
    match rows with
    | first :: _ :: _ ->
        let s, e = first
        if s <= 2 && foreign first then blankRows s e
        let s, e = List.last rows
        if e >= h - 3 && foreign (s, e) then blankRows s e
    | _ -> ()
    let colInk x = Seq.exists (fun y -> ink px.[y * w + x]) (seq { 0 .. h - 1 })
    // glyphs of one word or number, e.g. "(1)", count as one run
    let cols =
        runs (Array.init w colInk)
        |> List.fold (fun acc (s, e) ->
            match acc with
            | (ps, pe) :: rest when s - pe <= 18 -> (ps, e) :: rest
            | _ -> (s, e) :: acc) []
        |> List.rev
    let cols =
        match List.rev cols with
        | (ns, ne) :: (_, pe) :: _ when dropNumber && ns - pe > w / 12 && ne - ns < w / 8 ->
            for y in 0 .. h - 1 do Array.fill px (y * w + ns) (ne - ns + 1) white
            List.take (cols.Length - 1) cols
        | _ -> cols
    let rows = runs (Array.init h rowInk)
    match cols, rows with
    | [], _ | _, [] -> px, w, h
    | _ ->
        let pad = 8
        let x0 = max 0 (fst cols.Head - pad)
        let x1 = min (w - 1) (snd (List.last cols) + pad)
        let y0 = max 0 (fst rows.Head - pad)
        let y1 = min (h - 1) (snd (List.last rows) + pad)
        let cw, ch = x1 - x0 + 1, y1 - y0 + 1
        let out = Array.zeroCreate<int> (cw * ch)
        for y in 0 .. ch - 1 do Array.blit px ((y + y0) * w + x0) out (y * cw) cw
        out, cw, ch
