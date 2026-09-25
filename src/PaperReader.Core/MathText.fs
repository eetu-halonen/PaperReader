/// Recognising math in PDF text and saying it out loud.
module PaperReader.Core.MathText

open System
open System.Text

/// Strips the 6-letter subset prefix ("ABCDEF+CMMI10" -> "CMMI10").
let baseFontName (name: string) =
    if isNull name then ""
    else
        let i = name.IndexOf '+'
        if i = 6 then name.Substring 7 else name

let private mathFontMarkers =
    [| "CMMI"; "CMSY"; "CMEX"; "CMBSY"; "MSAM"; "MSBM"; "EUFM"; "EUSM"; "EUEX"; "RSFS"; "CMMIB"
       "LMMATH"; "LATINMODERNMATH"; "STIXMATH"; "STIXTWOMATH"; "CAMBRIAMATH"; "XITSMATH"
       "MTMI"; "MTSY"; "MTEX"; "TXMI"; "TXSY"; "TXEX"; "PXMI"; "PXSY"; "PXEX"; "NTXMI"; "NTXSY"; "RTXMI"
       "ESINT"; "BBOLD"; "DSROM"; "STMARY"; "WASY"; "MATHDESIGN"; "SYMBOL"; "MATH" |]

/// True for fonts used only for mathematics (TeX math fonts, Cambria Math, Symbol, ...).
let isMathFont (fontName: string) =
    let n = (baseFontName fontName).ToUpperInvariant().Replace("-", "").Replace(" ", "")
    mathFontMarkers |> Array.exists n.Contains

let isBoldFont (fontName: string) =
    let n = (baseFontName fontName).ToUpperInvariant()
    n.Contains "BOLD" || n.Contains "CMBX" || n.Contains "MEDI" || n.Contains "SEMIBOLD"
    || n.Contains "BLACK" || n.Contains "HEAVY" || n.EndsWith "-BD" || n.Contains ",BOLD"

let isItalicFont (fontName: string) =
    let n = (baseFontName fontName).ToUpperInvariant()
    n.Contains "ITAL" || n.Contains "CMTI" || n.Contains "OBLIQUE" || n.Contains "CMSL"

let private greek =
    dict [
        'α', "alpha"; 'β', "beta"; 'γ', "gamma"; 'δ', "delta"; 'ε', "epsilon"; 'ϵ', "epsilon"
        'ζ', "zeta"; 'η', "eta"; 'θ', "theta"; 'ϑ', "theta"; 'ι', "iota"; 'κ', "kappa"; 'λ', "lambda"
        'μ', "mu"; 'ν', "nu"; 'ξ', "xi"; 'π', "pi"; 'ϖ', "pi"; 'ρ', "rho"; 'ϱ', "rho"; 'σ', "sigma"
        'ς', "sigma"; 'τ', "tau"; 'υ', "upsilon"; 'φ', "phi"; 'ϕ', "phi"; 'χ', "chi"; 'ψ', "psi"
        'ω', "omega"; 'Γ', "capital gamma"; 'Δ', "capital delta"; 'Θ', "capital theta"
        'Λ', "capital lambda"; 'Ξ', "capital xi"; 'Π', "capital pi"; 'Σ', "capital sigma"
        'Υ', "capital upsilon"; 'Φ', "capital phi"; 'Ψ', "capital psi"; 'Ω', "capital omega"
    ]

let private symbols =
    dict [
        "=", "equals"; "≠", "is not equal to"; "≈", "is approximately"; "≃", "is approximately"
        "≅", "is congruent to"; "≡", "is equivalent to"; "∝", "is proportional to"; "∼", "is distributed as"
        "<", "is less than"; ">", "is greater than"; "≤", "is at most"; "≥", "is at least"
        "≪", "is much less than"; "≫", "is much greater than"; "⩽", "is at most"; "⩾", "is at least"
        "+", "plus"; "−", "minus"; "±", "plus or minus"; "∓", "minus or plus"; "×", "times"
        "·", "times"; "⋅", "times"; "∗", "star"; "÷", "divided by"; "/", "over"; "∘", "composed with"
        "⊙", "element-wise times"; "⊗", "tensor"; "⊕", "direct sum"
        "∑", "the sum of"; "∏", "the product of"; "∫", "the integral of"; "∮", "the contour integral of"
        "∂", "partial"; "∇", "the gradient of"; "√", "the square root of"; "∞", "infinity"
        "∈", "in"; "∉", "not in"; "∋", "contains"; "⊂", "is a subset of"; "⊆", "is a subset of"
        "⊃", "is a superset of"; "⊇", "is a superset of"; "∪", "union"; "∩", "intersected with"
        "∅", "the empty set"; "∀", "for all"; "∃", "there exists"; "¬", "not"; "∧", "and"; "∨", "or"
        "→", "to"; "↦", "maps to"; "←", "gets"; "⇒", "implies"; "⟹", "implies"; "⇔", "if and only if"
        "⟺", "if and only if"; "↔", "if and only if"; "|", " "; "‖", "norm"; "∥", "parallel to"
        "⊥", "perpendicular to"; "⊤", "transpose"; "⊺", "transpose"; "′", "prime"; "″", "double prime"
        "…", "dot dot dot"; "⋯", "dot dot dot"; "⋮", ""; "ℓ", "ell"; "ℏ", "h-bar"; "°", "degrees"
        "ℝ", "R"; "ℕ", "N"; "ℤ", "Z"; "ℚ", "Q"; "ℂ", "C"; "𝔼", "E"; "ˆ", "hat"; "̂", "hat"
        "¯", "bar"; "̄", "bar"; "˜", "tilde"; "̃", "tilde"; "˙", "dot"; "̇", "dot"; "⃗", "vector"
        "⌊", "floor of"; "⌋", ""; "⌈", "ceiling of"; "⌉", ""; "⟨", "the inner product of"; "⟩", ""
    ]

let private isMathBlock (c: char) =
    let code = int c
    (code >= 0x2200 && code <= 0x22FF) // mathematical operators
    || (code >= 0x2190 && code <= 0x21FF) // arrows
    || (code >= 0x27C0 && code <= 0x27FF)
    || (code >= 0x2980 && code <= 0x2AFF)
    || (code >= 0x0391 && code <= 0x03F6) // Greek
    || Char.IsSurrogate c // mathematical alphanumerics live above the BMP

/// True if a character only ever appears in formulas.
let isMathSymbol (s: string) =
    not (String.IsNullOrEmpty s)
    && (s |> Seq.exists isMathBlock
        || s = "=" || s = "<" || s = ">" || s = "±" || s = "×" || s = "÷" || s = "√" || s = "‖" || s = "·")

/// Spoken form of a single math glyph.
let speakGlyph (s: string) =
    if String.IsNullOrEmpty s then ""
    else
        let n = s.Normalize(NormalizationForm.FormKC)
        match symbols.TryGetValue n with
        | true, w -> " " + w + " "
        | _ ->
            match symbols.TryGetValue s with
            | true, w -> " " + w + " "
            | _ ->
                if n.Length = 1 then
                    match greek.TryGetValue n.[0] with
                    | true, w -> " " + w + " "
                    | _ -> n
                else n

/// How a run of superscript glyphs is read aloud after its base.
let speakSuperscript (content: string) =
    match content.Trim() with
    | "2" -> " squared "
    | "3" -> " cubed "
    | "T" | "⊤" | "⊺" -> " transpose "
    | "−1" | "-1" -> " inverse "
    | "∗" | "*" | "⋆" -> " star "
    | "′" | "'" -> " prime "
    | "" -> " "
    | c -> " to the power " + c + " "

/// Collapses whitespace and tidies spacing around punctuation.
let tidy (s: string) =
    let sb = StringBuilder(s.Length)
    let mutable lastSpace = true
    for c in s do
        if Char.IsWhiteSpace c then
            if not lastSpace then sb.Append ' ' |> ignore
            lastSpace <- true
        else
            sb.Append c |> ignore
            lastSpace <- false
    let s =
        Text.RegularExpressions.Regex.Replace(sb.ToString(), @"(?<!\.)\.\.(?!\.)", ".")
    s
      .Replace(" ,", ",")
      .Replace(" .", ".")
      .Replace(" ;", ";")
      .Replace(" :", ":")
      .Replace("( ", "(")
      .Replace(" )", ")")
      .Trim()
