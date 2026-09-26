/// Readers for text-based documents: plain text, Markdown and HTML (web pages, and the chapters of EPUB books).
/// Each turns its input into blocks (see Blocks).
module PaperReader.Core.Markup

open System
open System.Collections.Generic
open System.Net
open System.Text
open System.Text.RegularExpressions
open PaperReader.Core.Blocks

let private rx (p: string) = Regex(p, RegexOptions.Compiled ||| RegexOptions.CultureInvariant)

/// Decodes a data: URI's bytes (base64 or percent-encoded).
let dataUri (uri: string) : byte[] option =
    try
        let comma = uri.IndexOf ','
        if not (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) || comma < 0 then None
        else
            let meta = uri.Substring(5, comma - 5)
            let payload = uri.Substring(comma + 1)
            if meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) then Some(Convert.FromBase64String(payload.Trim()))
            else Some(Encoding.UTF8.GetBytes(Uri.UnescapeDataString payload))
    with _ -> None

/// Whitespace collapsed, invisible joiners and soft hyphens left out.
let private collapse (s: string) = Regex.Replace(Regex.Replace(s, "[\u00AD\u200B-\u200D\u2060\uFEFF]", ""), @"\s+", " ")

// ---------------------------------------------------------------------------------------------
// HTML: a small, forgiving parser into a tree
// ---------------------------------------------------------------------------------------------

type Element(name: string, attrs: Dictionary<string, string>) =
    member _.Name = name
    member _.Attrs = attrs
    member val Children = List<Node>()
    member this.Attr(key: string) =
        match attrs.TryGetValue key with
        | true, v -> v
        | _ -> ""
    member this.Elements = this.Children |> Seq.choose (function El e -> Some e | _ -> None)
    /// All elements below this one, depth first.
    member this.Descendants: seq<Element> =
        seq {
            for e in this.Elements do
                yield e
                yield! e.Descendants
        }

and Node =
    | El of Element
    | Txt of string

let private voidElements =
    set [ "area"; "base"; "br"; "col"; "embed"; "hr"; "img"; "input"; "link"; "meta"; "param"; "source"; "track"; "wbr"; "image" ]
let private rawElements = set [ "script"; "style"; "textarea"; "title"; "xmp"; "iframe"; "noembed"; "noframes"; "noscript" ]

let blockElements =
    set [ "address"; "article"; "aside"; "blockquote"; "body"; "center"; "details"; "dialog"; "dd"; "div"; "dl"; "dt"; "fieldset"
          "figcaption"; "figure"; "footer"; "form"; "h1"; "h2"; "h3"; "h4"; "h5"; "h6"; "header"; "hgroup"; "hr"; "li"; "main"
          "nav"; "ol"; "p"; "pre"; "section"; "table"; "tbody"; "td"; "tfoot"; "th"; "thead"; "tr"; "ul"; "summary"; "caption"; "html"
          "listing"; "menu"; "legend" ]

/// Parses HTML or XHTML, tolerating what browsers tolerate (unclosed paragraphs and list items, stray end tags).
let parseHtml (html: string) : Element =
    let root = Element("#root", Dictionary())
    let stack = List<Element>([ root ])
    let top () = stack.[stack.Count - 1]
    let localName (n: string) =
        let n = n.ToLowerInvariant()
        let colon = n.IndexOf ':'
        if colon >= 0 then n.Substring(colon + 1) else n
    let popTo (i: int) = stack.RemoveRange(i, stack.Count - i)
    let findOpen (names: Set<string>) (stopAt: Set<string>) =
        let mutable i = stack.Count - 1
        let mutable found = -1
        while found < 0 && i > 0 do
            let n = stack.[i].Name
            if names.Contains n then found <- i
            elif stopAt.Contains n then i <- 0
            i <- i - 1
        found
    let openElement (name: string) (attrs: Dictionary<string, string>) (selfClosing: bool) =
        // implied end tags
        let close (names: Set<string>) (stopAt: Set<string>) =
            let i = findOpen names stopAt
            if i > 0 then popTo i
        if blockElements.Contains name && top().Name = "p" then popTo (stack.Count - 1)
        match name with
        | "li" -> close (set [ "li" ]) (set [ "ul"; "ol"; "menu"; "table" ])
        | "dt" | "dd" -> close (set [ "dt"; "dd" ]) (set [ "dl"; "table" ])
        | "td" | "th" -> close (set [ "td"; "th" ]) (set [ "tr"; "table" ])
        | "tr" -> close (set [ "tr"; "td"; "th" ]) (set [ "table"; "thead"; "tbody"; "tfoot" ])
        | "thead" | "tbody" | "tfoot" -> close (set [ "thead"; "tbody"; "tfoot"; "tr"; "td"; "th" ]) (set [ "table" ])
        | "option" -> close (set [ "option" ]) (set [ "select" ])
        | _ -> ()
        let e = Element(name, attrs)
        top().Children.Add(El e)
        if not (selfClosing || voidElements.Contains name) then stack.Add e
        e
    let text = StringBuilder()
    let flushText () =
        if text.Length > 0 then
            top().Children.Add(Txt(WebUtility.HtmlDecode(text.ToString())))
            text.Clear() |> ignore
    let n = html.Length
    let mutable i = 0
    let indexFrom (s: string) (from: int) =
        let k = html.IndexOf(s, from, StringComparison.OrdinalIgnoreCase)
        if k < 0 then n else k
    while i < n do
        let c = html.[i]
        if c = '<' && i + 1 < n then
            let next = html.[i + 1]
            if html.Substring(i, min 4 (n - i)) = "<!--" then
                flushText ()
                i <- min n (indexFrom "-->" (i + 4) + 3)
            elif html.Substring(i, min 9 (n - i)).ToUpperInvariant() = "<![CDATA[" then
                let e = indexFrom "]]>" (i + 9)
                text.Append(WebUtility.HtmlEncode(html.Substring(i + 9, e - i - 9))) |> ignore
                i <- min n (e + 3)
            elif next = '!' || next = '?' then
                flushText ()
                i <- min n (indexFrom ">" i + 1)
            elif next = '/' && i + 2 < n && Char.IsLetter html.[i + 2] then
                flushText ()
                let e = indexFrom ">" i
                let name = localName (html.Substring(i + 2, e - i - 2).Trim())
                let name = (name.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead |> Option.defaultValue "")
                if name = "br" then openElement "br" (Dictionary()) true |> ignore
                elif name = "p" && findOpen (set [ "p" ]) Set.empty < 0 then openElement "p" (Dictionary()) true |> ignore
                else
                    let k = findOpen (set [ name ]) Set.empty
                    if k > 0 then popTo k
                i <- min n (e + 1)
            elif Char.IsLetter next then
                flushText ()
                // tag name
                let mutable j = i + 1
                while j < n && not (Char.IsWhiteSpace html.[j]) && html.[j] <> '>' && html.[j] <> '/' do j <- j + 1
                let name = localName (html.Substring(i + 1, j - i - 1))
                // attributes
                let attrs = Dictionary<string, string>()
                let mutable selfClosing = false
                let mutable fin = false
                while not fin && j < n do
                    while j < n && Char.IsWhiteSpace html.[j] do j <- j + 1
                    if j >= n then fin <- true
                    elif html.[j] = '>' then
                        j <- j + 1
                        fin <- true
                    elif html.[j] = '/' then
                        if j + 1 < n && html.[j + 1] = '>' then selfClosing <- true
                        j <- j + 1
                    else
                        let s = j
                        while j < n && not (Char.IsWhiteSpace html.[j]) && html.[j] <> '=' && html.[j] <> '>' && not (html.[j] = '/' && j + 1 < n && html.[j + 1] = '>') do j <- j + 1
                        let key = html.Substring(s, j - s).ToLowerInvariant()
                        while j < n && Char.IsWhiteSpace html.[j] do j <- j + 1
                        let value =
                            if j < n && html.[j] = '=' then
                                j <- j + 1
                                while j < n && Char.IsWhiteSpace html.[j] do j <- j + 1
                                if j < n && (html.[j] = '"' || html.[j] = ''') then
                                    let q = html.[j]
                                    let e = html.IndexOf(q, j + 1)
                                    let e = if e < 0 then n else e
                                    let v = html.Substring(j + 1, e - j - 1)
                                    j <- min n (e + 1)
                                    v
                                else
                                    let s = j
                                    while j < n && not (Char.IsWhiteSpace html.[j]) && html.[j] <> '>' do j <- j + 1
                                    html.Substring(s, j - s)
                            else ""
                        if key <> "" && not (attrs.ContainsKey key) then attrs.[key] <- WebUtility.HtmlDecode value
                let e = openElement name attrs selfClosing
                i <- j
                if rawElements.Contains name && not selfClosing then
                    let close = indexFrom ("</" + name) i
                    if close > i then e.Children.Add(Txt(if name = "title" || name = "textarea" then WebUtility.HtmlDecode(html.Substring(i, close - i)) else html.Substring(i, close - i)))
                    stack.Remove e |> ignore
                    i <- min n (indexFrom ">" close + 1)
            else
                text.Append c |> ignore
                i <- i + 1
        else
            text.Append c |> ignore
            i <- i + 1
    flushText ()
    root

/// All text below an element, as it is (no layout).
let rec textContent (e: Element) : string =
    let sb = StringBuilder()
    for c in e.Children do
        match c with
        | Txt t -> sb.Append t |> ignore
        | El x -> sb.Append(textContent x) |> ignore
    sb.ToString()

// ---------------------------------------------------------------------------------------------
// HTML to blocks
// ---------------------------------------------------------------------------------------------

/// How a reader finds a picture's bytes from its src attribute (a path inside an EPUB, an address on the web).
type Resolver = string -> ImageSource option

let private alwaysSkip =
    set [ "script"; "style"; "noscript"; "template"; "svg"; "iframe"; "object"; "embed"; "canvas"; "video"; "audio"; "button"
          "input"; "select"; "textarea"; "head"; "map"; "dialog"; "track"; "source" ]

/// Page furniture on the web: navigation, menus, sharing, cookie banners, comments, ads.
let private chromeWords =
    set [ "nav"; "navbar"; "navigation"; "menu"; "sidebar"; "footer"; "cookie"; "cookies"; "consent"; "share"; "sharing"; "social"
          "advert"; "advertisement"; "ad"; "ads"; "promo"; "breadcrumb"; "breadcrumbs"; "related"; "comments"; "comment"
          "subscribe"; "newsletter"; "signup"; "banner"; "popup"; "modal"; "toolbar"; "skip"; "masthead"; "mw-editsection"
          "noprint"; "sr-only"; "visually-hidden"; "ltx_page_footer"; "ltx_page_header"; "ltx_role_footnote"
          // Wikipedia's notes about the article, navigation boxes and reference lists
          "hatnote"; "navbox"; "vertical-navbox"; "catlinks"; "reflist"; "mw-references-wrap"; "references"; "ambox"
          "sistersitebox"; "metadata"; "mw-jump-link"; "shortdescription" ]

let private tokensOf (s: string) = s.ToLowerInvariant().Split([| ' '; '\t'; '\n'; '_'; '-' |], StringSplitOptions.RemoveEmptyEntries)

let private hidden (e: Element) =
    e.Attrs.ContainsKey "hidden"
    || e.Attr "aria-hidden" = "true"
    || Regex.IsMatch(e.Attr "style", @"display\s*:\s*none|visibility\s*:\s*hidden", RegexOptions.IgnoreCase)

/// The formulas in a hidden element: formulas are often kept as hidden MathML next to a picture of them
/// (Wikipedia), and the MathML is what is read. Nothing else of the element is.
let private hiddenMath (e: Element) =
    if hidden e then e.Descendants |> Seq.filter (fun d -> d.Name = "math") |> List.ofSeq else []

/// Mostly links: a menu, a list of other pages, an "Edit links" line. Not worth listening to.
let private linkFarm (e: Element) =
    match e.Name with
    | "ul" | "ol" | "p" | "div" | "table" | "section" | "dl" | "menu" ->
        let total = (textContent e).Trim().Length
        if total = 0 then false
        else
            let links = e.Descendants |> Seq.filter (fun d -> d.Name = "a") |> List.ofSeq
            let linked = links |> List.sumBy (fun a -> (textContent a).Trim().Length)
            float linked >= 0.8 * float total && (links.Length >= 3 || total < 30)
    | _ -> false

let private isChrome (e: Element) =
    match e.Name with
    | "nav" | "aside" | "form" -> true
    | _ ->
        let role = e.Attr "role"
        role = "navigation" || role = "banner" || role = "contentinfo" || role = "complementary" || role = "search"
        || (let cls = e.Attr "class" + " " + e.Attr "id"
            // whole classes ("site-footer") and their parts ("footer")
            cls.ToLowerInvariant().Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.exists chromeWords.Contains
            || tokensOf cls |> Array.exists (fun t -> t = "nav" || t = "sidebar" || t = "cookie" || t = "breadcrumb" || t = "share" || t = "comments" || t = "advert" || t = "newsletter"))

/// A footnote or citation mark: a superscript or link that only points somewhere else in the page.
let private isNoteMark (e: Element) =
    let t = e.Attr "epub:type" + " " + e.Attr "type" + " " + e.Attr "role" + " " + e.Attr "class"
    Regex.IsMatch(t, @"noteref|doc-noteref|footnote-ref|reference|ltx_note_mark|ltx_cite|citation", RegexOptions.IgnoreCase)
    || (e.Name = "sup"
        && (let links = e.Elements |> Seq.filter (fun x -> x.Name = "a") |> List.ofSeq
            not links.IsEmpty && links |> List.forall (fun a -> (a.Attr "href").StartsWith "#")
            && (textContent e).Trim().Length <= 8))

let private displayRx = rx @"\\displaystyle|\\textstyle"

/// LaTeX of a MathML element: its alttext or TeX annotation (what arXiv, Wikipedia and most converters write).
let private mathLatex (e: Element) : string option =
    let fromAnnotation () =
        e.Descendants
        |> Seq.tryFind (fun x -> x.Name = "annotation" && (x.Attr "encoding").ToLowerInvariant().Contains "tex")
        |> Option.map textContent
    let alt = e.Attr "alttext"
    let latex = if alt <> "" then Some alt else fromAnnotation ()
    latex
    |> Option.map (fun l ->
        let l = displayRx.Replace(l, "").Trim()
        // Wikipedia wraps formulas in {\displaystyle …}
        if l.StartsWith "{" && l.EndsWith "}" then l.Substring(1, l.Length - 2).Trim() else l)
    |> Option.filter (fun l -> l <> "")

let private isDisplayMath (e: Element) =
    e.Attr "display" = "block" || e.Attr "mode" = "display"

let private eqNumberRx = rx @"^\(\s*([A-Z]?[\d.]+[a-z]?)\s*\)$"

type private Walker(resolve: Resolver, web: bool) =
    let blocks = List<Block>()
    let buffer = StringBuilder()
    let mutable inItem = 0
    let mutable pendingImages = List<Block>()

    member _.Blocks = blocks

    member this.Flush() =
        // citations left out leave "(Llama 2, )" or "()" behind
        let t = Regex.Replace(buffer.ToString(), @"\(\s*[,;]?\s*\)|\[\s*[,;]?\s*\]", "")
        let t = MathText.tidy (Regex.Replace(t, @"\s*[,;]\s*\)", ")"))
        buffer.Clear() |> ignore
        if t |> Seq.exists Char.IsLetterOrDigit then
            blocks.Add(if inItem > 0 then Block.Item t else Block.Paragraph t)
        // pictures met inside the text come after it
        blocks.AddRange pendingImages
        pendingImages.Clear()

    member this.Skip(e: Element) =
        alwaysSkip.Contains e.Name || hidden e || (web && (isChrome e || linkFarm e))
        || (e.Attr "class").Contains "mwe-math-fallback" || (e.Attr "class").Contains "ltx_note"

    /// Inline text of an element: sub/superscripts marked, math as $LaTeX$, notes left out.
    member this.Inline(e: Element, sb: StringBuilder) =
        for c in e.Children do
            match c with
            | Txt t -> sb.Append(collapse t) |> ignore
            | El x when not (hiddenMath x).IsEmpty ->
                for m in hiddenMath x do
                    match mathLatex m with
                    | Some l -> sb.Append(" $").Append(l).Append("$ ") |> ignore
                    | None -> ()
            | El x when this.Skip x || isNoteMark x -> ()
            | El x ->
                match x.Name with
                | "br" -> sb.Append ' ' |> ignore
                | "math" ->
                    match mathLatex x with
                    | Some l -> sb.Append(" $").Append(l).Append("$ ") |> ignore
                    | None -> sb.Append(collapse (textContent x)) |> ignore
                | "sup" ->
                    let inner = StringBuilder()
                    this.Inline(x, inner)
                    let s = inner.ToString().Trim()
                    if s <> "" then sb.Append("^{").Append(s).Append("}") |> ignore
                | "sub" ->
                    let inner = StringBuilder()
                    this.Inline(x, inner)
                    let s = inner.ToString().Trim()
                    if s <> "" then sb.Append("_{").Append(s).Append("}") |> ignore
                | "img" -> ()
                | _ ->
                    if blockElements.Contains x.Name then sb.Append ' ' |> ignore
                    this.Inline(x, sb)
                    if blockElements.Contains x.Name then sb.Append ' ' |> ignore

    member this.InlineText(e: Element) =
        let sb = StringBuilder()
        this.Inline(e, sb)
        MathText.tidy (sb.ToString())

    member this.Image(e: Element, caption: string) : Block option =
        let src =
            [ "data-src"; "data-original"; "src"; "href"; "xlink:href" ]
            |> List.map e.Attr
            |> List.tryFind (fun s -> s <> "" && not (s.StartsWith "data:image/gif")) // lazy-loading placeholders
        let small =
            let size (k: string) = match Int32.TryParse(Regex.Match(e.Attr k, @"\d+").Value) with | true, v -> Some v | _ -> None
            match size "width", size "height" with
            | Some w, Some h -> w < 48 && h < 48
            | _ -> false
        match src with
        | Some s when not small ->
            let source = if s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) then dataUri s |> Option.map Data else resolve s
            // "[Uncaptioned image]", "image", "photo.jpg" say nothing
            let alt = MathText.tidy (e.Attr "alt")
            let alt = if Regex.IsMatch(alt, @"^\[.*\]$|^(image|img|picture|photo|figure|graphic)$|\.(png|jpe?g|gif|webp|svg)$", RegexOptions.IgnoreCase) then "" else alt
            source |> Option.map (fun src -> Block.Image(src, alt, caption))
        | _ -> None

    member this.Rows(table: Element) : string list list =
        // the table's own rows, not those of tables nested in its cells
        let rec rows (e: Element) : Element list =
            [ for x in e.Elements do
                  match x.Name with
                  | "tr" -> yield x
                  | "thead" | "tbody" | "tfoot" -> yield! rows x
                  | _ -> () ]
        [ for tr in rows table ->
              [ for cell in tr.Elements do
                    if cell.Name = "td" || cell.Name = "th" then
                        yield this.InlineText cell ] ]

    /// A table used for layout (web pages of old) rather than for data: one column, tables or headings inside,
    /// or cells holding whole paragraphs of text.
    member this.IsLayoutTable(t: Element) =
        let rows = this.Rows t
        let cells = rows |> List.concat
        rows.Length <= 1 || rows |> List.forall (fun r -> r.Length <= 1)
        || t.Descendants |> Seq.exists (fun d -> d.Name = "table" || d.Name.Length = 2 && d.Name.[0] = 'h' && Char.IsDigit d.Name.[1])
        || (cells |> List.sumBy String.length) / max 1 cells.Length > 250

    member this.Figure(e: Element) =
        this.Flush()
        let caption =
            e.Descendants |> Seq.tryFind (fun x -> x.Name = "figcaption" || x.Name = "caption")
            |> Option.map this.InlineText |> Option.defaultValue ""
        let tables = e.Descendants |> Seq.filter (fun x -> x.Name = "table") |> List.ofSeq
        let images = e.Descendants |> Seq.filter (fun x -> (x.Name = "img" || x.Name = "image") && not (this.Skip x)) |> List.ofSeq
        let code = e.Descendants |> Seq.tryFind (fun x -> x.Name = "pre")
        let maths = e.Descendants |> Seq.filter (fun x -> x.Name = "math" && isDisplayMath x) |> List.ofSeq
        match tables, images, code with
        | t :: _, _, _ when maths.IsEmpty && not (this.IsLayoutTable t) -> blocks.Add(Block.Table(this.Rows t, caption))
        | _, img :: rest, _ ->
            let first = this.Image(img, caption)
            match first with
            | Some b -> blocks.Add b
            | None -> if caption <> "" then blocks.Add(Block.Paragraph caption)
            for r in rest do this.Image(r, "") |> Option.iter blocks.Add
        | _, [], Some pre -> blocks.Add(Block.Code(textContent pre, caption))
        | _ ->
            for c in e.Children do
                match c with
                | El x when x.Name = "figcaption" -> ()
                | c -> this.Walk c
            this.Flush()
            if caption <> "" then blocks.Add(Block.Paragraph caption)

    /// A table that holds display equations and their numbers (how LaTeX converters lay out numbered equations).
    member this.EquationTable(t: Element) =
        let maths = t.Descendants |> Seq.filter (fun x -> x.Name = "math") |> List.ofSeq
        if maths.IsEmpty then false
        else
            let cells = t.Descendants |> Seq.filter (fun x -> x.Name = "td") |> List.ofSeq
            let prose = cells |> List.exists (fun c -> (c.Descendants |> Seq.forall (fun d -> d.Name <> "math")) && (textContent c).Trim().Length > 30)
            if prose && not ((t.Attr "class").Contains "equation" || (t.Attr "class").Contains "eqn") then false
            else
                this.Flush()
                // one numbered row per equation (an equation array numbers the rows it wants to)
                for tr in t.Descendants |> Seq.filter (fun x -> x.Name = "tr") do
                    let rowMaths = tr.Descendants |> Seq.filter (fun x -> x.Name = "math") |> List.ofSeq
                    let number =
                        tr.Descendants
                        |> Seq.filter (fun x -> x.Name = "td" || x.Name = "span")
                        |> Seq.tryPick (fun c -> let m = eqNumberRx.Match((textContent c).Trim()) in if m.Success then Some m.Groups.[1].Value else None)
                    let latex = rowMaths |> List.choose mathLatex
                    if not latex.IsEmpty then blocks.Add(Block.Math(String.Join(" ", latex), number))
                true

    member this.Walk(node: Node) =
        match node with
        | Txt t -> buffer.Append(collapse t) |> ignore
        | El e when not (hiddenMath e).IsEmpty -> for m in hiddenMath e do this.Walk(El m)
        | El e when this.Skip e || isNoteMark e -> ()
        | El e ->
            match e.Name with
            | "h1" | "h2" | "h3" | "h4" | "h5" | "h6" ->
                this.Flush()
                let t = this.InlineText e
                if t <> "" then blocks.Add(Block.Heading(int e.Name.[1] - int '0', t))
            | "br" -> buffer.Append ' ' |> ignore
            | "hr" -> this.Flush()
            | "img" | "image" ->
                // a picture inside running text is shown after the paragraph
                this.Image(e, "") |> Option.iter pendingImages.Add
            | "figure" -> this.Figure e
            | "pre" ->
                this.Flush()
                blocks.Add(Block.Code((textContent e).TrimEnd(), ""))
            | "math" ->
                match mathLatex e with
                | Some l when isDisplayMath e ->
                    this.Flush()
                    blocks.Add(Block.Math(l, None))
                | Some l -> buffer.Append(" $").Append(l).Append("$ ") |> ignore
                | None -> buffer.Append(collapse (textContent e)) |> ignore
            | "sup" | "sub" ->
                let s = this.InlineText e
                if s <> "" then buffer.Append(if e.Name = "sup" then "^{" else "_{").Append(s).Append("}") |> ignore
            | "table" when this.EquationTable e -> ()
            | "table" when not (this.IsLayoutTable e) ->
                this.Flush()
                let caption = e.Elements |> Seq.tryFind (fun x -> x.Name = "caption") |> Option.map this.InlineText |> Option.defaultValue ""
                blocks.Add(Block.Table(this.Rows e, caption))
            | "li" | "dt" | "dd" ->
                this.Flush()
                inItem <- inItem + 1
                for c in e.Children do this.Walk c
                this.Flush()
                inItem <- inItem - 1
            | "ul" | "ol" | "dl" ->
                // a list starts items afresh even inside another item
                this.Flush()
                let outer = inItem
                inItem <- 0
                for c in e.Children do this.Walk c
                this.Flush()
                inItem <- outer
            | name when blockElements.Contains name ->
                this.Flush()
                for c in e.Children do this.Walk c
                this.Flush()
            | _ ->
                for c in e.Children do this.Walk c

    member this.Run(root: Element) =
        this.Walk(El root)
        this.Flush()

/// The largest element with this name, by its amount of text.
let private largest (root: Element) (pred: Element -> bool) =
    root.Descendants |> Seq.filter pred |> Seq.sortByDescending (fun e -> (textContent e).Length) |> Seq.tryHead

/// The part of a web page that holds the article: <article>, <main>, or the body.
let private contentRoot (doc: Element) =
    let body = doc.Descendants |> Seq.tryFind (fun e -> e.Name = "body") |> Option.defaultValue doc
    let bodyLength = max 1 (textContent body).Length
    let candidate =
        largest body (fun e -> e.Name = "article")
        |> Option.orElse (largest body (fun e -> e.Name = "main" || e.Attr "role" = "main"))
        |> Option.orElse (largest body (fun e -> e.Attr "id" = "content" || e.Attr "id" = "main-content" || (e.Attr "class").Contains "ltx_document"))
    match candidate with
    // a stray <article> (a teaser, a comment) isn't the page's content
    | Some e when (textContent e).Length >= 400 || float (textContent e).Length > 0.3 * float bodyLength -> e
    | _ -> body

/// The page's title: its first main heading, or <title> (without " | Site name").
let htmlTitle (doc: Element) =
    let meta (names: string list) =
        doc.Descendants
        |> Seq.tryFind (fun e -> e.Name = "meta" && names |> List.exists (fun n -> e.Attr "property" = n || e.Attr "name" = n))
        |> Option.map (fun e -> e.Attr "content")
    let title = doc.Descendants |> Seq.tryFind (fun e -> e.Name = "title") |> Option.map textContent
    let h1 = doc.Descendants |> Seq.tryFind (fun e -> e.Name = "h1") |> Option.map (textContent >> collapse)
    // " - Wikipedia", " | Site name"
    let withoutSite (t: string) = Regex.Split(t, @"\s+[|–—·]\s+|\s+-\s+").[0]
    [ meta [ "citation_title"; "dc.title"; "DC.title" ]; h1; meta [ "og:title" ] |> Option.map withoutSite; title |> Option.map withoutSite ]
    |> List.choose id
    |> List.map (fun t -> MathText.tidy t)
    |> List.tryFind (fun t -> t <> "" && t.Length < 300)

/// The address relative links are resolved against: <base href> when the page has one.
let htmlBase (doc: Element) =
    doc.Descendants |> Seq.tryFind (fun e -> e.Name = "base") |> Option.map (fun e -> e.Attr "href") |> Option.filter ((<>) "")

let private blocksOf (doc: Element) (resolve: Resolver) (web: bool) : Block list =
    let root =
        if web then contentRoot doc
        else doc.Descendants |> Seq.tryFind (fun e -> e.Name = "body") |> Option.defaultValue doc
    let w = Walker(resolve, web)
    w.Run root
    List.ofSeq w.Blocks

/// Blocks of an HTML document. `web` enables leaving out page furniture (menus, sidebars, footers) and
/// picking the article out of the page; EPUB chapters are read whole.
let htmlBlocks (html: string) (resolve: Resolver) (web: bool) : Block list = blocksOf (parseHtml html) resolve web

/// A web page (or an HTML file saved from one): its article, with pictures linked from the page's address
/// (<base href>, which pages saved by the app carry) to be fetched at import.
let webPage (html: string) (fallbackTitle: string) : Document =
    let doc = parseHtml html
    let baseUri = htmlBase doc |> Option.bind (fun b -> match Uri.TryCreate(b, UriKind.Absolute) with | true, u -> Some u | _ -> None)
    let resolve (src: string) =
        match Uri.TryCreate(src, UriKind.Absolute) with
        | true, u when u.Scheme = "http" || u.Scheme = "https" -> Some(Link u.AbsoluteUri)
        | _ ->
            match baseUri with
            | Some b -> match Uri.TryCreate(b, src) with | true, u -> Some(Link u.AbsoluteUri) | _ -> None
            | None -> None // a local file's pictures aren't with it
    { Title = htmlTitle doc |> Option.defaultValue fallbackTitle
      Source = "a web page (article text taken from the page)"
      Blocks = blocksOf doc resolve true }

let private referencesRx =
    rx @"^((\d{1,2}|[IVX]{1,4})\.?\s+)?(references|bibliography|literature cited|works cited|notes and references|citations|sources|see also|external links|further reading|footnotes|notes)$"

/// Leaves out the list of references (and on web pages the links and notes after the article): not worth
/// listening to. A later appendix, or anything under a higher heading, is kept.
let dropReferences (blocks: Block list) =
    let mutable skipping: int option = None
    [ for b in blocks do
          match b, skipping with
          | Block.Heading (level, text), _ when referencesRx.IsMatch((text.Trim().TrimEnd('.', ':')).ToLowerInvariant()) ->
              skipping <- Some level
          | Block.Heading (level, _), Some l when level <= l -> skipping <- None; yield b
          | Block.Break, Some _ -> yield b
          | _, Some _ -> ()
          | _, None -> yield b ]

// ---------------------------------------------------------------------------------------------
// Markdown
// ---------------------------------------------------------------------------------------------

let private atxRx = rx @"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$"
let private fenceRx = rx @"^\s{0,3}(```+|~~~+)"
let private itemRx = rx @"^\s*([-*+•]|\d{1,3}[.)])\s+(.*)$"
let private ruleRx = rx @"^\s{0,3}([-*_])(\s*\1){2,}\s*$"
let private tableSepRx = rx @"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$"
let private imageLineRx = rx @"^\s*!\[([^\]]*)\]\(\s*<?([^)\s>]+)>?(?:\s+""([^""]*)"")?\s*\)\s*$"
let private inlineImageRx = rx @"!\[([^\]]*)\]\(\s*<?([^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)"
let private linkRx = rx @"\[([^\]]+)\]\((?:[^()]|\([^)]*\))*\)"
let private refLinkRx = rx @"\[([^\]]+)\]\[[^\]]*\]"
let private footRefRx = rx @"\[\^[^\]]+\]"
let private autoLinkRx = rx @"<(https?://[^>]+)>"
let private tagRx = rx @"</?[a-zA-Z][^>]*>"
let private boldRx = rx @"(\*\*|__)(?=\S)(.+?)(?<=\S)\1"
let private italicRx = rx @"(?<![\w*])([*_])(?=\S)(.+?)(?<=\S)\1(?![\w*])"
let private codeRx = rx @"`+([^`]+)`+"
let private strikeRx = rx @"~~(.+?)~~"
let private inlineParenMathRx = rx @"\\\((.+?)\\\)"

/// Markdown inline syntax removed: links keep their text, emphasis and code marks go. Math ($…$, \(…\))
/// is kept as $LaTeX$.
let inlineText (s: string) =
    let spans = List<string>()
    let s = inlineParenMathRx.Replace(s, fun m -> "$" + m.Groups.[1].Value + "$")
    let masked = Regex.Replace(s, @"\$\$(.+?)\$\$|\$([^$\n]+?)\$", fun m -> spans.Add m.Value; sprintf "\u0001%d\u0002" (spans.Count - 1))
    let t = inlineImageRx.Replace(masked, "")
    let t = linkRx.Replace(t, "$1")
    let t = refLinkRx.Replace(t, "$1")
    let t = footRefRx.Replace(t, "")
    let t = autoLinkRx.Replace(t, "$1")
    let t = Regex.Replace(t, @"<sup>(.*?)</sup>", "^{$1}", RegexOptions.IgnoreCase)
    let t = Regex.Replace(t, @"<sub>(.*?)</sub>", "_{$1}", RegexOptions.IgnoreCase)
    let t = tagRx.Replace(t, " ")
    let t = boldRx.Replace(t, "$2")
    let t = italicRx.Replace(t, "$2")
    let t = strikeRx.Replace(t, "$1")
    let t = codeRx.Replace(t, "$1")
    let t = Regex.Replace(t, @"\\([\\`*_{}\[\]()#+\-.!|])", "$1")
    let t = WebUtility.HtmlDecode t
    let t = Regex.Replace(t, "\u0001(\\d+)\u0002", fun m -> spans.[int m.Groups.[1].Value])
    MathText.tidy t

let private cells (line: string) =
    let t = line.Trim()
    let t = if t.StartsWith "|" then t.Substring 1 else t
    let t = if t.EndsWith "|" && not (t.EndsWith @"\|") then t.Substring(0, t.Length - 1) else t
    Regex.Split(t, @"(?<!\\)\|") |> Array.map (fun c -> inlineText (c.Trim())) |> List.ofArray

/// Blocks of a Markdown text (CommonMark and GitHub tables, $…$ and $$…$$ math, as Mistral OCR writes it).
/// HTML inside it (OCR writes some tables as HTML) is read as HTML.
let markdownBlocks (markdown: string) (resolve: Resolver) : Block list =
    let lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
    let blocks = List<Block>()
    let para = List<string>()
    let mutable item: string option = None
    let flushPara () =
        if para.Count > 0 then
            let raw = String.Join(" ", para)
            let text = inlineText raw
            if text |> Seq.exists Char.IsLetterOrDigit then blocks.Add(Block.Paragraph text)
            // pictures inside the paragraph follow it
            for m in inlineImageRx.Matches raw do
                resolve m.Groups.[2].Value |> Option.iter (fun src -> blocks.Add(Block.Image(src, inlineText m.Groups.[1].Value, "")))
            para.Clear()
        match item with
        | Some t ->
            let text = inlineText t
            if text <> "" then blocks.Add(Block.Item text)
            item <- None
        | None -> ()
    let mutable i = 0
    let n = lines.Length
    while i < n do
        let line = lines.[i]
        let trimmed = line.Trim()
        let fence = fenceRx.Match line
        if trimmed = "" then
            flushPara ()
            i <- i + 1
        elif fence.Success then
            flushPara ()
            let marker = fence.Groups.[1].Value
            let lang = line.Trim().Substring(marker.Length).Trim().ToLowerInvariant()
            let body = List<string>()
            i <- i + 1
            while i < n && not (lines.[i].TrimStart().StartsWith marker) do
                body.Add lines.[i]
                i <- i + 1
            i <- i + 1
            let text = String.Join("\n", body)
            if lang = "math" || lang = "latex" || lang = "tex" then blocks.Add(Block.Math(text.Trim(), None))
            else blocks.Add(Block.Code(text, ""))
        elif trimmed.StartsWith "$$" || trimmed.StartsWith @"\[" then
            flushPara ()
            let closeMark = if trimmed.StartsWith "$$" then "$$" else @"\]"
            let first = trimmed.Substring 2
            let body = StringBuilder()
            if first.Contains closeMark then
                body.Append(first.Substring(0, first.IndexOf closeMark)) |> ignore
                i <- i + 1
            else
                body.AppendLine first |> ignore
                i <- i + 1
                while i < n && not (lines.[i].Contains closeMark) do
                    body.AppendLine lines.[i] |> ignore
                    i <- i + 1
                if i < n then
                    body.Append(lines.[i].Substring(0, lines.[i].IndexOf closeMark)) |> ignore
                    i <- i + 1
            let latex = body.ToString().Trim()
            if latex <> "" then blocks.Add(Block.Math(latex, None))
        elif atxRx.IsMatch line then
            flushPara ()
            let m = atxRx.Match line
            let text = inlineText m.Groups.[2].Value
            if text <> "" then blocks.Add(Block.Heading(m.Groups.[1].Value.Length, text))
            i <- i + 1
        elif para.Count = 1 && item.IsNone && (Regex.IsMatch(trimmed, @"^=+$") || Regex.IsMatch(trimmed, @"^-+$")) then
            // setext heading: the paragraph line above is the heading
            let text = inlineText para.[0]
            para.Clear()
            blocks.Add(Block.Heading((if trimmed.[0] = '=' then 1 else 2), text))
            i <- i + 1
        elif ruleRx.IsMatch line then
            flushPara ()
            i <- i + 1
        elif trimmed.Contains "|" && i + 1 < n && tableSepRx.IsMatch lines.[i + 1] && lines.[i + 1].Contains "-" then
            flushPara ()
            let rows = List<string list>([ cells line ])
            i <- i + 2
            while i < n && lines.[i].Trim() <> "" && lines.[i].Contains "|" do
                rows.Add(cells lines.[i])
                i <- i + 1
            blocks.Add(Block.Table(List.ofSeq rows, ""))
        elif imageLineRx.IsMatch line then
            flushPara ()
            let m = imageLineRx.Match line
            let alt = inlineText m.Groups.[1].Value
            match resolve m.Groups.[2].Value with
            | Some src ->
                // OCR names pictures "img-0.jpeg"; many writers put the caption in the alt text
                let alt = if Regex.IsMatch(alt, @"^img-\d+\.\w+$") then "" else alt
                let title = inlineText m.Groups.[3].Value
                let caption = if title <> "" then title elif isCaption alt then alt else ""
                blocks.Add(Block.Image(src, (if caption = alt then "" else alt), caption))
            | None -> ()
            i <- i + 1
        elif trimmed.StartsWith "<" && para.Count = 0 && Regex.IsMatch(trimmed, @"^<(table|div|figure|p|ul|ol|h[1-6]|blockquote|pre|img|math|section|details)\b", RegexOptions.IgnoreCase) then
            flushPara ()
            // an HTML block runs to the next blank line
            let body = StringBuilder()
            while i < n && lines.[i].Trim() <> "" do
                body.AppendLine lines.[i] |> ignore
                i <- i + 1
            blocks.AddRange(htmlBlocks (body.ToString()) resolve false)
        elif trimmed.StartsWith ">" then
            // a quote is read as the text it quotes
            item |> Option.iter (fun _ -> flushPara ())
            let t = trimmed.TrimStart('>').Trim()
            if t = "" then flushPara () else para.Add t
            i <- i + 1
        elif itemRx.IsMatch line then
            flushPara ()
            item <- Some(itemRx.Match(line).Groups.[2].Value)
            i <- i + 1
        else
            match item with
            | Some t when line.StartsWith "  " || line.StartsWith "\t" || para.Count = 0 -> item <- Some(t + " " + trimmed)
            | _ -> para.Add trimmed
            i <- i + 1
    flushPara ()
    List.ofSeq blocks

// ---------------------------------------------------------------------------------------------
// Plain text
// ---------------------------------------------------------------------------------------------

let private chapterRx = rx @"^(chapter|part|book|section|prologue|epilogue|preface|introduction|conclusion|appendix|contents|foreword|afterword)\b"
let private numberedRx = rx @"^(\d{1,2}(\.\d{1,2}){0,3}\.?|[IVXLC]{1,6}\.)\s+\S"

/// Blocks of a plain text: paragraphs at blank lines (or at every line when there are none), headings by
/// their look (chapter titles, numbered or ALL CAPS lines standing alone), form feeds as page breaks.
/// Project Gutenberg's licence header and footer are left out.
let textBlocks (text: string) : Block list =
    let text = text.Replace("\r\n", "\n").Replace('\r', '\n')
    let text =
        let start = Regex.Match(text, @"^\*\*\*\s*START OF (THE|THIS) PROJECT GUTENBERG.*$", RegexOptions.Multiline ||| RegexOptions.IgnoreCase)
        let text = if start.Success then text.Substring(start.Index + start.Length) else text
        let fin = Regex.Match(text, @"^\*\*\*\s*END OF (THE|THIS) PROJECT GUTENBERG.*$", RegexOptions.Multiline ||| RegexOptions.IgnoreCase)
        if fin.Success then text.Substring(0, fin.Index) else text
    let pages = text.Split('\f')
    let blocks = List<Block>()
    pages |> Array.iteri (fun p pageText ->
        if p > 0 then blocks.Add Block.Break
        let hasBlankLines = Regex.IsMatch(pageText, @"\n[ \t]*\n")
        let paras =
            if hasBlankLines then Regex.Split(pageText, @"\n[ \t]*\n")
            else pageText.Split('\n')
        let paras = paras |> Array.map (fun s -> s.Trim('\n', ' ', '\t')) |> Array.filter (fun s -> s <> "")
        paras |> Array.iteri (fun k para ->
            let lines = para.Split('\n') |> Array.map (fun l -> l.Trim())
            // hard-wrapped lines are joined; a word broken at the line end is mended
            let joined =
                lines
                |> Array.fold (fun (acc: string) (l: string) ->
                    if acc = "" then l
                    elif acc.EndsWith "-" && acc.Length > 1 && Char.IsLetter acc.[acc.Length - 2] && l.Length > 0 && Char.IsLower l.[0] then acc.Substring(0, acc.Length - 1) + l
                    else acc + " " + l) ""
            let single = lines.Length = 1
            let noEnd = not (Regex.IsMatch(joined, @"[.,;:!?""”’)]$"))
            let letters = joined |> Seq.filter Char.IsLetter |> Seq.length
            let caps = letters >= 3 && joined |> Seq.filter Char.IsLetter |> Seq.forall Char.IsUpper
            let nextLong = k + 1 < paras.Length && paras.[k + 1].Length > 150
            let heading =
                single && joined.Length <= 80 && letters > 0
                && ((chapterRx.IsMatch joined && joined.Length <= 60)
                    || (noEnd && (caps || numberedRx.IsMatch joined) && hasBlankLines)
                    || (noEnd && nextLong && hasBlankLines && Char.IsUpper joined.[0] && joined.Split(' ').Length <= 10))
            if heading then
                let level = if chapterRx.IsMatch joined || caps then 1 else 2
                let t = if caps then Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(joined.ToLowerInvariant()) else joined
                blocks.Add(Block.Heading(level, t))
            elif not (Regex.IsMatch(joined, @"^[\W_]+$")) then blocks.Add(Block.Paragraph joined)))
    List.ofSeq blocks
