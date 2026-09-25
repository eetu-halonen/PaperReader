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

/// Compatibility normalization (FormKC) isn't available in the browser.
let private kcSupported =
    try
        "ﬁ".Normalize(NormalizationForm.FormKC) |> ignore
        true
    with _ -> false

let private upperGreek = "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡϴΣΤΥΦΧΨΩ"
let private lowerGreek = "αβγδεζηθικλμνξοπρςστυφχψω"

let private folded =
    dict [ "ﬀ", "ff"; "ﬁ", "fi"; "ﬂ", "fl"; "ﬃ", "ffi"; "ﬄ", "ffl"; "ﬅ", "st"; "ﬆ", "st"; "ℎ", "h"; "ℓ", "l"; "…", "..."
           " ", " "; "⁰", "0"; "¹", "1"; "²", "2"; "³", "3"; "⁴", "4"; "⁵", "5"; "⁶", "6"; "⁷", "7"; "⁸", "8"; "⁹", "9"
           "₀", "0"; "₁", "1"; "₂", "2"; "₃", "3"; "₄", "4"; "₅", "5"; "₆", "6"; "₇", "7"; "₈", "8"; "₉", "9"
           "ℝ", "R"; "ℕ", "N"; "ℤ", "Z"; "ℚ", "Q"; "ℂ", "C"; "ℒ", "L"; "ℱ", "F"; "ℋ", "H"; "ℐ", "I"; "ℛ", "R"; "ℬ", "B"
           "ℰ", "E"; "ℳ", "M"; "ℊ", "g"; "ℯ", "e"; "ℴ", "o"
           "ⁱ", "i"; "ⁿ", "n"; "ᵀ", "T"; "ᵢ", "i"; "ⱼ", "j"; "ₖ", "k"; "ₘ", "m"; "ₙ", "n"; "ₜ", "t"; "ₓ", "x"
           "⁺", "+"; "⁻", "−"; "₊", "+"; "₋", "−"; "⁼", "="; "₌", "="; "⁽", "("; "⁾", ")"; "₍", "("; "₎", ")" ]

/// Folds presentation variants away by hand: ligatures, math italic and bold letters (𝑥 → x, 𝜃 → θ),
/// super- and subscript digits. What FormKC does for the characters papers use.
let fold (s: string) =
        let sb = StringBuilder(s.Length)
        let mutable i = 0
        while i < s.Length do
            let cp = Char.ConvertToUtf32(s, i)
            let width = if Char.IsSurrogatePair(s, i) then 2 else 1
            if cp >= 0x1D400 && cp <= 0x1D6A3 then
                // Mathematical Alphanumeric Symbols: 13 styles of A–Z a–z
                let k = (cp - 0x1D400) % 52
                sb.Append(if k < 26 then char (int 'A' + k) else char (int 'a' + k - 26)) |> ignore
            elif cp >= 0x1D6A8 && cp <= 0x1D7C9 then
                // 5 styles of Greek: 25 capitals, nabla, 25 small letters, partial, 6 variants
                let k = (cp - 0x1D6A8) % 58
                if k < 25 then sb.Append(upperGreek.[k]) |> ignore
                elif k = 25 then sb.Append('∇') |> ignore
                elif k < 51 then sb.Append(lowerGreek.[k - 26]) |> ignore
                elif k = 51 then sb.Append('∂') |> ignore
                else sb.Append("εθκφρπ".[k - 52]) |> ignore
            elif cp >= 0x1D7CE && cp <= 0x1D7FF then
                sb.Append(char (int '0' + (cp - 0x1D7CE) % 10)) |> ignore
            else
                let c = s.Substring(i, width)
                match folded.TryGetValue c with
                | true, f -> sb.Append(f) |> ignore
                | _ -> sb.Append(c) |> ignore
            i <- i + width
        sb.ToString()

/// Compatibility normalization: FormKC where the runtime has it, otherwise `fold`.
let compat (s: string) = if kcSupported then s.Normalize(NormalizationForm.FormKC) else fold s

/// Spoken form of a single math glyph.
let speakGlyph (s: string) =
    if String.IsNullOrEmpty s then ""
    else
        let n = compat s
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
