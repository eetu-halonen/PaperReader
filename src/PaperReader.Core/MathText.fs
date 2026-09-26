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

// ---- LaTeX read aloud (documents that carry their math as LaTeX or MathML rather than as glyphs)

let private latexWords =
    dict [
        "alpha", "alpha"; "beta", "beta"; "gamma", "gamma"; "delta", "delta"; "epsilon", "epsilon"; "varepsilon", "epsilon"
        "zeta", "zeta"; "eta", "eta"; "theta", "theta"; "vartheta", "theta"; "iota", "iota"; "kappa", "kappa"
        "lambda", "lambda"; "mu", "mu"; "nu", "nu"; "xi", "xi"; "pi", "pi"; "varpi", "pi"; "rho", "rho"; "varrho", "rho"
        "sigma", "sigma"; "varsigma", "sigma"; "tau", "tau"; "upsilon", "upsilon"; "phi", "phi"; "varphi", "phi"
        "chi", "chi"; "psi", "psi"; "omega", "omega"; "Gamma", "capital gamma"; "Delta", "capital delta"
        "Theta", "capital theta"; "Lambda", "capital lambda"; "Xi", "capital xi"; "Pi", "capital pi"
        "Sigma", "capital sigma"; "Upsilon", "capital upsilon"; "Phi", "capital phi"; "Psi", "capital psi"; "Omega", "capital omega"
        "cdot", "times"; "times", "times"; "div", "divided by"; "pm", "plus or minus"; "mp", "minus or plus"; "ast", "star"; "star", "star"
        "circ", "composed with"; "odot", "element-wise times"; "otimes", "tensor"; "oplus", "direct sum"
        "leq", "is at most"; "le", "is at most"; "leqslant", "is at most"; "geq", "is at least"; "ge", "is at least"; "geqslant", "is at least"
        "neq", "is not equal to"; "ne", "is not equal to"; "approx", "is approximately"; "simeq", "is approximately"; "sim", "is distributed as"
        "equiv", "is equivalent to"; "cong", "is congruent to"; "propto", "is proportional to"; "ll", "is much less than"; "gg", "is much greater than"
        "in", "in"; "notin", "not in"; "ni", "contains"; "subset", "is a subset of"; "subseteq", "is a subset of"; "supset", "is a superset of"
        "supseteq", "is a superset of"; "cup", "union"; "cap", "intersected with"; "emptyset", "the empty set"; "varnothing", "the empty set"
        "forall", "for all"; "exists", "there exists"; "neg", "not"; "lnot", "not"; "land", "and"; "wedge", "and"; "lor", "or"; "vee", "or"
        "to", "to"; "rightarrow", "to"; "longrightarrow", "to"; "mapsto", "maps to"; "leftarrow", "gets"; "gets", "gets"
        "Rightarrow", "implies"; "implies", "implies"; "Longrightarrow", "implies"; "iff", "if and only if"; "Leftrightarrow", "if and only if"
        "leftrightarrow", "if and only if"; "infty", "infinity"; "partial", "partial"; "nabla", "the gradient of"; "ell", "ell"; "hbar", "h-bar"
        "ldots", "dot dot dot"; "cdots", "dot dot dot"; "dots", "dot dot dot"; "dotsc", "dot dot dot"; "dotsb", "dot dot dot"; "dotsm", "dot dot dot"; "vdots", ""; "ddots", ""; "top", "transpose"; "intercal", "transpose"
        "perp", "perpendicular to"; "parallel", "parallel to"; "mid", "given"; "vert", " "; "lVert", "norm"; "rVert", ""; "Vert", "norm"
        "langle", "the inner product of"; "rangle", ""; "lfloor", "floor of"; "rfloor", ""; "lceil", "ceiling of"; "rceil", ""
        "prime", "prime"; "deg", "degrees"; "degree", "degrees"; "quad", " "; "qquad", " "; "colon", ":"
        "log", "log"; "ln", "natural log"; "exp", "exp"; "sin", "sine"; "cos", "cosine"; "tan", "tangent"; "max", "max"; "min", "min"
        "arg", "arg"; "argmax", "arg max"; "argmin", "arg min"; "sup", "sup"; "inf", "inf"; "lim", "the limit"; "det", "determinant of"
        "tr", "trace of"; "dim", "dimension of"; "ker", "kernel of"; "Pr", "probability of"; "E", "E"; "mathbb", ""; "sum", "the sum"
        "prod", "the product"; "int", "the integral"; "iint", "the double integral"; "oint", "the contour integral"
        "bigcup", "the union"; "bigcap", "the intersection" ]

/// Commands whose argument is read as it is (fonts, text, operators by name).
let private latexPassThrough =
    set [ "mathbf"; "mathrm"; "mathit"; "mathsf"; "mathtt"; "mathcal"; "mathscr"; "mathfrak"; "mathbb"; "boldsymbol"; "bm"
          "text"; "textrm"; "textit"; "textbf"; "mbox"; "operatorname"; "operatorname*"; "mathop"; "displaystyle"; "textstyle"
          "scriptstyle"; "mathnormal"; "emph"; "underline"; "overline*"; "boxed"; "phantom"; "hphantom"; "vphantom" ]

/// Accents read after their base ("x hat").
let private latexAccents =
    dict [ "hat", "hat"; "widehat", "hat"; "bar", "bar"; "overline", "bar"; "tilde", "tilde"; "widetilde", "tilde"; "vec", "vector"
           "dot", "dot"; "ddot", "double dot"; "check", "check"; "breve", "breve"; "overrightarrow", "vector" ]

/// Sizing and spacing commands that say nothing.
let private latexSilent =
    set [ "left"; "right"; "big"; "Big"; "bigg"; "Bigg"; "bigl"; "bigr"; "Bigl"; "Bigr"; "biggl"; "biggr"; "middle"; "limits"; "nolimits"
          "nonumber"; "notag"; "label"; "tag"; "begin"; "end"; "hline"; "centering"; "displaystyle"; "textstyle"; "scriptstyle" ]

type private LatexToken =
    | Command of string
    | Open
    | Close
    | Sup
    | Sub
    | Symbol of string
    /// Spaces separate words in \text{…} but never are an argument.
    | Space

let private latexTokens (latex: string) =
    let out = Collections.Generic.List<LatexToken>()
    let mutable i = 0
    while i < latex.Length do
        let c = latex.[i]
        if c = '\\' && i + 1 < latex.Length then
            if Char.IsLetter latex.[i + 1] then
                let start = i + 1
                i <- i + 1
                while i < latex.Length && Char.IsLetter latex.[i] do i <- i + 1
                let name = latex.Substring(start, i - start)
                if i < latex.Length && latex.[i] = '*' then i <- i + 1
                out.Add(Command name)
            else
                // \, \; \! \{ \} \| \\
                let s = latex.[i + 1]
                out.Add(match s with '{' -> Symbol "{" | '}' -> Symbol "}" | '|' -> Symbol "‖" | '\\' -> Symbol "," | _ -> Space)
                i <- i + 2
        else
            out.Add(match c with
                    | '{' -> Open
                    | '}' -> Close
                    | '^' -> Sup
                    | '_' -> Sub
                    | '&' | '~' -> Space
                    | c when Char.IsWhiteSpace c -> Space
                    | c -> Symbol(string c))
            i <- i + 1
    out.ToArray()

/// LaTeX math read aloud the way a lecturer says it: "x sub i squared", "a over b", "the sum over i of ...".
/// Unknown commands are read by name; it never throws.
let speakLatex (latex: string) : string =
    try
        let tokens = latexTokens latex
        let mutable pos = 0
        let peek () = if pos < tokens.Length then Some tokens.[pos] else None
        // the next token that isn't a space (arguments and limits skip spaces: "\frac {a} {b}", "\sum _{i}")
        let peekArg () =
            while pos < tokens.Length && tokens.[pos] = Space do pos <- pos + 1
            peek ()
        let sb () = StringBuilder()
        // an environment name after \begin / \end is skipped
        let skipGroup () =
            if peekArg () = Some Open then
                let mutable depth = 0
                let mutable fin = false
                while not fin && pos < tokens.Length do
                    match tokens.[pos] with
                    | Open -> depth <- depth + 1; pos <- pos + 1
                    | Close ->
                        depth <- depth - 1
                        pos <- pos + 1
                        if depth = 0 then fin <- true
                    | _ -> pos <- pos + 1
        let rec group () : string =
            // one argument: {…} or a single token
            match peekArg () with
            | Some Open ->
                pos <- pos + 1
                let s = sequence true
                if peek () = Some Close then pos <- pos + 1
                s
            | Some _ -> atom ()
            | None -> ""
        and optionalArg () =
            // [n] of \sqrt[n]{x}
            match peekArg () with
            | Some (Symbol "[") ->
                pos <- pos + 1
                let b = sb ()
                while pos < tokens.Length && tokens.[pos] <> Symbol "]" do
                    b.Append(atom ()) |> ignore
                if pos < tokens.Length then pos <- pos + 1
                Some(b.ToString().Trim())
            | _ -> None
        and scripts () =
            // limits of a big operator: _{…} and ^{…} in either order
            let mutable lower, upper = None, None
            let mutable go = true
            while go do
                match peekArg () with
                | Some Sub when lower.IsNone -> pos <- pos + 1; lower <- Some(group ())
                | Some Sup when upper.IsNone -> pos <- pos + 1; upper <- Some(group ())
                | Some (Command ("limits" | "nolimits")) -> pos <- pos + 1
                | _ -> go <- false
            lower, upper
        and atom () : string =
            match peek () with
            | None -> ""
            | Some t ->
                pos <- pos + 1
                match t with
                | Open ->
                    let s = sequence true
                    if peek () = Some Close then pos <- pos + 1
                    s
                | Close -> ""
                | Space -> " "
                | Sup ->
                    // \top, \prime, \ast are read already as words
                    match (group ()).Trim() with
                    | "transpose" | "prime" | "star" as w -> " " + w + " "
                    | "dot dot dot" -> " to the power dot dot dot "
                    | g -> speakSuperscript g
                | Sub -> " sub " + group () + " "
                | Symbol "-" -> " minus "
                | Symbol s -> speakGlyph s
                | Command name ->
                    match name with
                    | "frac" | "dfrac" | "tfrac" | "cfrac" ->
                        let a = group ()
                        let b = group ()
                        let simple (s: string) = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2
                        if simple a && simple b then sprintf " %s over %s " a b
                        else sprintf " %s, all over %s, " a b
                    | "binom" | "tbinom" | "dbinom" ->
                        let a = group ()
                        let b = group ()
                        sprintf " %s choose %s " a b
                    | "sqrt" ->
                        match optionalArg () with
                        | Some n -> sprintf " the %s-th root of %s " n (group ())
                        | None -> sprintf " the square root of %s " (group ())
                    | "sum" | "prod" | "int" | "iint" | "oint" | "bigcup" | "bigcap" | "lim" | "max" | "min" | "sup" | "inf" | "argmax" | "argmin" ->
                        let word = latexWords.[name]
                        match scripts () with
                        | Some l, Some u -> sprintf " %s over %s to %s of " word l u
                        | Some l, None when name = "lim" -> sprintf " %s as %s of " word l
                        | Some l, None -> sprintf " %s over %s of " word l
                        | None, Some u -> sprintf " %s to %s of " word u
                        | None, None -> sprintf " %s of " word
                    | "begin" | "end" -> skipGroup (); " "
                    | "label" | "tag" | "ref" | "eqref" | "hspace" | "vspace" | "color" -> skipGroup (); " "
                    | "textcolor" -> skipGroup (); group ()
                    | n when latexPassThrough.Contains n -> group ()
                    | n when latexAccents.ContainsKey n -> sprintf " %s %s " (group ()) latexAccents.[n]
                    | n when latexSilent.Contains n -> ""
                    | n ->
                        match latexWords.TryGetValue n with
                        | true, w -> " " + w + " "
                        | _ -> " " + n + " "
        and sequence (inGroup: bool) : string =
            let b = sb ()
            let mutable go = true
            while go do
                match peek () with
                | None -> go <- false
                | Some Close when inGroup -> go <- false
                | Some Close -> pos <- pos + 1
                | Some _ -> b.Append(atom ()) |> ignore
            b.ToString()
        let spoken = sequence false
        tidy (spoken.Replace(" ,", ","))
    with _ -> tidy latex
