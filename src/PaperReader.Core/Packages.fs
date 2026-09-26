/// Readers for documents that are zip packages of XML: EPUB books, Word (DOCX), OpenDocument text (ODT) and
/// PowerPoint (PPTX). Each turns its package into blocks (see Blocks); pictures come out of the package.
module PaperReader.Core.Packages

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.RegularExpressions
open System.Xml
open System.Xml.Linq
open PaperReader.Core.Blocks

/// Text of a file whatever its encoding: byte order marks, then UTF-8, then Latin-1.
let decodeText (b: byte[]) : string =
    if b.Length >= 3 && b.[0] = 0xEFuy && b.[1] = 0xBBuy && b.[2] = 0xBFuy then Encoding.UTF8.GetString(b, 3, b.Length - 3)
    elif b.Length >= 2 && b.[0] = 0xFFuy && b.[1] = 0xFEuy then Encoding.Unicode.GetString(b, 2, b.Length - 2)
    elif b.Length >= 2 && b.[0] = 0xFEuy && b.[1] = 0xFFuy then Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2)
    else
        try (UTF8Encoding(false, true)).GetString b
        with _ -> Encoding.Latin1.GetString b

type Package(bytes: byte[]) =
    let zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)
    let entries =
        zip.Entries |> Seq.map (fun e -> e.FullName, e) |> Seq.distinctBy (fun (n, _) -> n.ToLowerInvariant())
        |> Seq.map (fun (n, e) -> n.ToLowerInvariant(), e) |> dict
    member _.Names = zip.Entries |> Seq.map (fun e -> e.FullName) |> List.ofSeq
    member _.Has(path: string) = entries.ContainsKey(path.TrimStart('/').ToLowerInvariant())
    member this.Bytes(path: string) : byte[] option =
        match entries.TryGetValue(path.TrimStart('/').ToLowerInvariant()) with
        | true, e ->
            use s = e.Open()
            use m = new MemoryStream()
            s.CopyTo m
            Some(m.ToArray())
        | _ -> None
    member this.Text(path: string) = this.Bytes path |> Option.map decodeText
    member this.Xml(path: string) : XDocument option =
        this.Bytes path
        |> Option.bind (fun b ->
            try
                let settings = XmlReaderSettings(DtdProcessing = DtdProcessing.Ignore, XmlResolver = null)
                use r = XmlReader.Create(new MemoryStream(b), settings)
                Some(XDocument.Load r)
            with _ -> None)
    interface IDisposable with
        member _.Dispose() = zip.Dispose()

/// A path relative to the folder of `from` (both inside a package), with ./ and ../ resolved.
let resolvePath (from: string) (relative: string) =
    let relative = Uri.UnescapeDataString(relative.Split('#').[0])
    if relative.StartsWith "/" then relative.TrimStart('/')
    else
        let dir = match from.LastIndexOf '/' with -1 -> "" | k -> from.Substring(0, k + 1)
        let parts = Collections.Generic.List<string>()
        for p in (dir + relative).Split('/') do
            match p with
            | "." | "" -> ()
            | ".." -> if parts.Count > 0 then parts.RemoveAt(parts.Count - 1)
            | p -> parts.Add p
        String.Join("/", parts)

let private local (x: XElement) = x.Name.LocalName
let private attr (x: XElement) (name: string) =
    x.Attributes() |> Seq.tryFind (fun a -> a.Name.LocalName = name) |> Option.map (fun a -> a.Value) |> Option.defaultValue ""
let private children (x: XElement) (name: string) = x.Elements() |> Seq.filter (fun e -> local e = name)
let private descendants (x: XContainer) (name: string) = x.Descendants() |> Seq.filter (fun e -> local e = name)
let private first (x: XContainer) (name: string) = descendants x name |> Seq.tryHead

/// Relationship ids to their targets, from a part's .rels file (DOCX, PPTX).
let private relationships (pkg: Package) (part: string) =
    let dir, file = match part.LastIndexOf '/' with -1 -> "", part | k -> part.Substring(0, k + 1), part.Substring(k + 1)
    match pkg.Xml(dir + "_rels/" + file + ".rels") with
    | Some x ->
        descendants x "Relationship"
        |> Seq.map (fun r -> attr r "Id", (if attr r "TargetMode" = "External" then attr r "Target" else resolvePath part (attr r "Target")))
        |> dict
    | None -> dict []

/// The document's title from its core properties (docProps/core.xml, meta.xml), if it has one.
let private coreTitle (pkg: Package) =
    [ "docProps/core.xml"; "meta.xml" ]
    |> List.tryPick (fun p -> pkg.Xml p |> Option.bind (fun x -> first x "title") |> Option.map (fun t -> t.Value.Trim()))
    |> Option.filter (fun t -> t <> "")

// ---------------------------------------------------------------------------------------------
// EPUB
// ---------------------------------------------------------------------------------------------

/// An EPUB book: its chapters in reading order (the spine), each read as HTML and starting a new "page".
let epub (pkg: Package) (fallbackTitle: string) : Document =
    let opfPath =
        pkg.Xml "META-INF/container.xml"
        |> Option.bind (fun x -> first x "rootfile")
        |> Option.map (fun r -> attr r "full-path")
        |> Option.orElse (pkg.Names |> List.tryFind (fun n -> n.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)))
        |> Option.defaultWith (fun () -> failwith "This EPUB has no package file (content.opf).")
    let opf = pkg.Xml opfPath |> Option.defaultWith (fun () -> failwith "This EPUB's package file can't be read.")
    let title = first opf "title" |> Option.map (fun t -> t.Value.Trim()) |> Option.filter ((<>) "") |> Option.defaultValue fallbackTitle
    let manifest =
        descendants opf "item" |> Seq.map (fun i -> attr i "id", (resolvePath opfPath (attr i "href"), attr i "media-type", attr i "properties")) |> dict
    let spine =
        descendants opf "itemref"
        |> Seq.filter (fun r -> attr r "linear" <> "no")
        |> Seq.choose (fun r -> match manifest.TryGetValue(attr r "idref") with | true, (p, t, _) -> Some(p, t) | _ -> None)
        |> Seq.filter (fun (_, t) -> t.Contains "html" || t = "")
        |> List.ofSeq
    // the navigation document and cover page carry nothing to listen to
    let skip =
        manifest.Values |> Seq.filter (fun (_, _, props) -> props.Contains "nav") |> Seq.map (fun (p, _, _) -> p) |> set
    let blocks =
        [ for path, _ in spine do
              if not (skip.Contains path) then
                  match pkg.Text path with
                  | Some html ->
                      let resolve (src: string) = pkg.Bytes(resolvePath path src) |> Option.map Data
                      let chapter = Markup.htmlBlocks html resolve false
                      // a cover or title page is only pictures: keep the book from opening with a stray image
                      let hasText = chapter |> List.exists (function Block.Paragraph _ | Block.Heading _ | Block.Item _ -> true | _ -> false)
                      if hasText then
                          yield Block.Break
                          yield! chapter
                  | None -> () ]
    { Title = title; Source = "an EPUB book (each page is a chapter)"; Blocks = blocks }

// ---------------------------------------------------------------------------------------------
// Office Open XML math (OMML) to LaTeX
// ---------------------------------------------------------------------------------------------

let private naryLatex =
    dict [ "∑", @"\sum"; "∏", @"\prod"; "∫", @"\int"; "∬", @"\iint"; "∮", @"\oint"; "⋃", @"\bigcup"; "⋂", @"\bigcap" ]

/// Word's equations (OMML) as LaTeX: fractions, scripts, radicals, big operators, delimiters and runs.
let rec omml (x: XElement) : string =
    let arg (name: string) = children x name |> Seq.tryHead |> Option.map omml |> Option.defaultValue ""
    let prop (name: string) (key: string) =
        children x (local x + "Pr") |> Seq.tryHead |> Option.bind (fun p -> children p name |> Seq.tryHead) |> Option.map (fun e -> attr e key)
    match local x with
    | "t" -> x.Value
    | "f" -> sprintf @"\frac{%s}{%s}" (arg "num") (arg "den")
    | "sSup" -> sprintf "{%s}^{%s}" (arg "e") (arg "sup")
    | "sSub" -> sprintf "{%s}_{%s}" (arg "e") (arg "sub")
    | "sSubSup" -> sprintf "{%s}_{%s}^{%s}" (arg "e") (arg "sub") (arg "sup")
    | "sPre" -> sprintf "{}_{%s}^{%s}{%s}" (arg "sub") (arg "sup") (arg "e")
    | "rad" ->
        let deg = arg "deg"
        if deg = "" then sprintf @"\sqrt{%s}" (arg "e") else sprintf @"\sqrt[%s]{%s}" deg (arg "e")
    | "nary" ->
        let chr = prop "chr" "val" |> Option.defaultValue "∫"
        let op = match naryLatex.TryGetValue chr with | true, l -> l | _ -> chr
        sprintf "%s_{%s}^{%s}{%s}" op (arg "sub") (arg "sup") (arg "e")
    | "d" ->
        let o = prop "begChr" "val" |> Option.defaultValue "("
        let c = prop "endChr" "val" |> Option.defaultValue ")"
        let inner = children x "e" |> Seq.map omml |> String.concat ", "
        sprintf @"\left%s %s \right%s" (if o = "" then "." elif o = "{" then @"\{" else o) inner (if c = "" then "." elif c = "}" then @"\}" else c)
    | "acc" ->
        let chr = prop "chr" "val" |> Option.defaultValue "̂"
        let cmd = match chr with "̄" | "¯" -> @"\bar" | "̃" | "~" -> @"\tilde" | "⃗" | "→" -> @"\vec" | "̇" -> @"\dot" | _ -> @"\hat"
        sprintf "%s{%s}" cmd (arg "e")
    | "bar" -> sprintf @"\overline{%s}" (arg "e")
    | "func" -> sprintf "%s %s" (arg "fName") (arg "e")
    | "limLow" -> sprintf @"%s_{%s}" (arg "e") (arg "lim")
    | "limUpp" -> sprintf @"%s^{%s}" (arg "e") (arg "lim")
    | "m" ->
        let rows = children x "mr" |> Seq.map (fun r -> children r "e" |> Seq.map omml |> String.concat " & ")
        sprintf @"\begin{matrix}%s\end{matrix}" (String.Join(@" \\ ", rows))
    | "eqArr" -> children x "e" |> Seq.map omml |> String.concat @" \\ "
    | n when n.EndsWith "Pr" -> ""
    | _ -> x.Elements() |> Seq.map omml |> String.concat ""

// ---------------------------------------------------------------------------------------------
// Word (DOCX)
// ---------------------------------------------------------------------------------------------

/// A Word document: headings by their style (or outline level), lists, tables, pictures, captions and equations.
let docx (pkg: Package) (fallbackTitle: string) : Document =
    let doc = pkg.Xml "word/document.xml" |> Option.defaultWith (fun () -> failwith "This Word file has no document part.")
    let rels = relationships pkg "word/document.xml"
    // style id -> (name, outline level)
    let styles =
        match pkg.Xml "word/styles.xml" with
        | Some s ->
            descendants s "style"
            |> Seq.map (fun st ->
                let name = children st "name" |> Seq.tryHead |> Option.map (fun n -> attr n "val") |> Option.defaultValue ""
                let outline = descendants st "outlineLvl" |> Seq.tryHead |> Option.map (fun o -> int (attr o "val"))
                attr st "styleId", (name.ToLowerInvariant(), outline))
            |> Seq.distinctBy fst
            |> dict
        | None -> dict []
    let styleOf (p: XElement) =
        first p "pStyle" |> Option.map (fun s -> attr s "val") |> Option.defaultValue ""
    let headingLevel (p: XElement) =
        let id = styleOf p
        let name, outline = match styles.TryGetValue id with | true, s -> s | _ -> id.ToLowerInvariant(), None
        let own = first p "outlineLvl" |> Option.map (fun o -> int (attr o "val"))
        let m = Regex.Match(name, @"^heading\s*(\d)")
        if name = "title" then Some 0
        elif m.Success then Some(int m.Groups.[1].Value)
        else own |> Option.orElse outline |> Option.filter (fun l -> l < 9) |> Option.map ((+) 1)
    let isCaptionStyle (p: XElement) =
        let id = styleOf p
        let name = match styles.TryGetValue id with | true, (n, _) -> n | _ -> id.ToLowerInvariant()
        name = "caption"
    let image (x: XElement) =
        first x "blip"
        |> Option.bind (fun b ->
            match rels.TryGetValue(attr b "embed") with
            | true, target -> pkg.Bytes target
            | _ -> None)
        |> Option.map (fun bytes ->
            let alt = first x "docPr" |> Option.map (fun d -> attr d "descr") |> Option.defaultValue ""
            Block.Image(Data bytes, alt, ""))
    /// A paragraph's text, with its inline equations as $LaTeX$, and the pictures it holds.
    let rec runs (x: XElement) (sb: StringBuilder) (pictures: Collections.Generic.List<Block>) =
        for e in x.Elements() do
            match local e with
            | "t" -> sb.Append(e.Value) |> ignore
            | "tab" | "br" | "cr" -> sb.Append ' ' |> ignore
            | "noBreakHyphen" -> sb.Append '-' |> ignore
            | "sym" -> sb.Append ' ' |> ignore
            | "oMath" -> sb.Append(" $").Append(omml e).Append("$ ") |> ignore
            | "drawing" | "pict" | "object" -> image e |> Option.iter pictures.Add
            | "footnoteReference" | "endnoteReference" | "commentReference" | "instrText" | "delText" | "rPr" | "pPr" -> ()
            | "r" ->
                // superscripts and subscripts, as extracted text marks them
                let va = first e "vertAlign" |> Option.map (fun v -> attr v "val") |> Option.defaultValue ""
                let inner = StringBuilder()
                runs e inner pictures
                let t = inner.ToString()
                if t.Trim() <> "" && va = "superscript" then sb.Append("^{").Append(t.Trim()).Append("}") |> ignore
                elif t.Trim() <> "" && va = "subscript" then sb.Append("_{").Append(t.Trim()).Append("}") |> ignore
                else sb.Append t |> ignore
            | _ -> runs e sb pictures
    let blocks = Collections.Generic.List<Block>()
    let mutable title: string option = None
    let rec body (container: XElement) =
        for e in container.Elements() do
            match local e with
            | "p" ->
                let display = children e "oMathPara" |> List.ofSeq
                if not display.IsEmpty then
                    for para in display do
                        for m in children para "oMath" do blocks.Add(Block.Math(omml m, None))
                else
                    let sb = StringBuilder()
                    let pictures = Collections.Generic.List<Block>()
                    runs e sb pictures
                    let text = MathText.tidy (sb.ToString())
                    let listItem = (first e "numPr").IsSome
                    match headingLevel e with
                    | Some 0 when text <> "" && title.IsNone -> title <- Some text
                    | Some level when text <> "" -> blocks.Add(Block.Heading(max 1 level, text))
                    | _ when text <> "" -> blocks.Add(if listItem then Block.Item text else Block.Paragraph text)
                    | _ -> ()
                    blocks.AddRange pictures
                    // a Caption-styled paragraph that doesn't say "Figure N" still captions the picture before it
                    if isCaptionStyle e && text <> "" && not (isCaption text) && blocks.Count >= 2 then
                        match blocks.[blocks.Count - 2] with
                        | Block.Image (src, alt, "") ->
                            blocks.RemoveAt(blocks.Count - 1)
                            blocks.[blocks.Count - 1] <- Block.Image(src, alt, text)
                        | _ -> ()
            | "tbl" ->
                let rows =
                    [ for tr in children e "tr" ->
                          [ for tc in children tr "tc" ->
                                let sb = StringBuilder()
                                let pics = Collections.Generic.List<Block>()
                                for p in children tc "p" do
                                    runs p sb pics
                                    sb.Append ' ' |> ignore
                                MathText.tidy (sb.ToString()) ] ]
                // a one-cell table is a text box, not data
                match rows with
                | [ [ cell ] ] -> if cell <> "" then blocks.Add(Block.Paragraph cell)
                | _ -> blocks.Add(Block.Table(rows, ""))
            | "sdt" -> children e "sdtContent" |> Seq.iter body
            | _ -> ()
    first doc "body" |> Option.iter body
    { Title = title |> Option.orElse (coreTitle pkg) |> Option.defaultValue fallbackTitle
      Source = "a Word document"
      Blocks = List.ofSeq blocks }

// ---------------------------------------------------------------------------------------------
// OpenDocument text (ODT)
// ---------------------------------------------------------------------------------------------

/// An OpenDocument text: headings (text:h), paragraphs, lists, tables and pictures.
let odt (pkg: Package) (fallbackTitle: string) : Document =
    let content = pkg.Xml "content.xml" |> Option.defaultWith (fun () -> failwith "This OpenDocument file has no content.")
    let blocks = Collections.Generic.List<Block>()
    let mutable title: string option = None
    // paragraph styles to the named styles they derive from ("P1" -> "Heading_20_1", "Title")
    let parents =
        [ content; yield! pkg.Xml "styles.xml" |> Option.toList ]
        |> Seq.collect (fun x -> descendants x "style")
        |> Seq.map (fun st -> attr st "name", attr st "parent-style-name")
        |> Seq.distinctBy fst
        |> dict
    let rec styleLevel (name: string) (depth: int) =
        let m = Regex.Match(name, @"^Heading_20_(\d)$")
        if m.Success then Some(int m.Groups.[1].Value)
        elif name = "Title" then Some 0
        elif depth > 5 then None
        else match parents.TryGetValue name with | true, p when p <> "" -> styleLevel p (depth + 1) | _ -> None
    let rec text (x: XElement) (sb: StringBuilder) =
        for n in x.Nodes() do
            match n with
            | :? XText as t -> sb.Append(t.Value) |> ignore
            | :? XElement as e ->
                match local e with
                | "s" | "tab" | "line-break" -> sb.Append ' ' |> ignore
                | "note" | "annotation" | "frame" -> ()
                | _ -> text e sb
            | _ -> ()
    let textOf (x: XElement) =
        let sb = StringBuilder()
        text x sb
        MathText.tidy (sb.ToString())
    let pictures (x: XElement) =
        for img in descendants x "image" do
            let href = attr img "href"
            match pkg.Bytes href with
            | Some bytes -> blocks.Add(Block.Image(Data bytes, "", ""))
            | None -> ()
    let rec body (x: XElement) (inList: bool) =
        for e in x.Elements() do
            match local e with
            | "h" ->
                let t = textOf e
                let level = match Int32.TryParse(attr e "outline-level") with | true, l -> l | _ -> 1
                if t <> "" then blocks.Add(Block.Heading(level, t))
            | "p" ->
                let t = textOf e
                match styleLevel (attr e "style-name") 0 with
                | Some 0 when t <> "" && title.IsNone -> title <- Some t
                | Some level when t <> "" -> blocks.Add(Block.Heading(max 1 level, t))
                | _ -> if t <> "" then blocks.Add(if inList then Block.Item t else Block.Paragraph t)
                pictures e
            | "list" -> body e true
            | "list-item" | "list-header" | "section" -> body e inList
            | "table" ->
                let rows =
                    [ for tr in descendants e "table-row" -> [ for tc in children tr "table-cell" -> textOf tc ] ]
                blocks.Add(Block.Table(rows, ""))
            | _ -> ()
    first content "text" |> Option.iter (fun t -> body t false)
    { Title = title |> Option.orElse (coreTitle pkg) |> Option.defaultValue fallbackTitle; Source = "an OpenDocument text"; Blocks = List.ofSeq blocks }

// ---------------------------------------------------------------------------------------------
// PowerPoint (PPTX)
// ---------------------------------------------------------------------------------------------

/// A slide deck: each slide starts a new page, its title is a heading, its text boxes are read in order,
/// with its pictures and tables, then the speaker notes.
let pptx (pkg: Package) (fallbackTitle: string) : Document =
    let pres = pkg.Xml "ppt/presentation.xml" |> Option.defaultWith (fun () -> failwith "This PowerPoint file has no presentation part.")
    let rels = relationships pkg "ppt/presentation.xml"
    let slides =
        descendants pres "sldId"
        |> Seq.choose (fun s ->
            let rid = s.Attributes() |> Seq.tryFind (fun a -> a.Name.LocalName = "id" && a.Name.NamespaceName <> "") |> Option.map (fun a -> a.Value)
            rid |> Option.bind (fun r -> match rels.TryGetValue r with | true, t -> Some t | _ -> None))
        |> List.ofSeq
    let paragraphText (p: XElement) =
        let sb = StringBuilder()
        for e in p.Descendants() do
            match local e with
            | "t" -> sb.Append(e.Value) |> ignore
            | "br" -> sb.Append ' ' |> ignore
            | _ -> ()
        MathText.tidy (sb.ToString())
    let blocks = Collections.Generic.List<Block>()
    let mutable deckTitle: string option = None
    for k, slidePath in List.indexed slides do
        match pkg.Xml slidePath with
        | None -> ()
        | Some slide ->
            let srels = relationships pkg slidePath
            blocks.Add Block.Break
            let mutable titled = false
            let tree = first slide "spTree"
            let rec shapes (x: XElement) =
                for e in x.Elements() do
                    match local e with
                    | "sp" ->
                        let ph = first e "ph" |> Option.map (fun p -> attr p "type")
                        let paras = descendants e "p" |> Seq.map paragraphText |> Seq.filter ((<>) "") |> List.ofSeq
                        match ph with
                        | Some ("title" | "ctrTitle") when not paras.IsEmpty ->
                            let t = String.Join(" ", paras)
                            if k = 0 && deckTitle.IsNone then deckTitle <- Some t
                            blocks.Add(Block.Heading(1, t))
                            titled <- true
                        | Some ("sldNum" | "dt" | "ftr" | "hdr") -> ()
                        | Some "subTitle" -> for p in paras do blocks.Add(Block.Paragraph p)
                        | _ ->
                            // bullet points are list items; a lone text box reads as a paragraph
                            for p in paras do blocks.Add(if paras.Length > 1 then Block.Item p else Block.Paragraph p)
                    | "pic" ->
                        let id = first e "blip" |> Option.map (fun b -> attr b "embed") |> Option.defaultValue ""
                        let alt = first e "cNvPr" |> Option.map (fun c -> attr c "descr") |> Option.defaultValue ""
                        match srels.TryGetValue id with
                        | true, target -> pkg.Bytes target |> Option.iter (fun bytes -> blocks.Add(Block.Image(Data bytes, alt, "")))
                        | _ -> ()
                    | "graphicFrame" ->
                        match first e "tbl" with
                        | Some tbl ->
                            let rows = [ for tr in children tbl "tr" -> [ for tc in children tr "tc" -> descendants tc "p" |> Seq.map paragraphText |> String.concat " " ] ]
                            blocks.Add(Block.Table(rows, ""))
                        | None -> ()
                    | "grpSp" -> shapes e
                    | _ -> ()
            tree |> Option.iter shapes
            if not titled then blocks.Insert(blocks.LastIndexOf Block.Break + 1, Block.Heading(1, sprintf "Slide %d" (k + 1)))
            // speaker notes say what the slide leaves out
            let notes =
                srels.Values
                |> Seq.tryFind (fun t -> t.Contains "notesSlide")
                |> Option.bind pkg.Xml
                |> Option.map (fun n ->
                    descendants n "sp"
                    |> Seq.filter (fun sp -> first sp "ph" |> Option.exists (fun p -> attr p "type" = "body"))
                    |> Seq.collect (fun sp -> descendants sp "p" |> Seq.map paragraphText)
                    |> Seq.filter ((<>) "")
                    |> List.ofSeq)
                |> Option.defaultValue []
            for n in notes do blocks.Add(Block.Paragraph n)
    { Title = coreTitle pkg |> Option.orElse deckTitle |> Option.defaultValue fallbackTitle
      Source = "a slide deck (each page is a slide; speaker notes follow the slide's own text)"
      Blocks = List.ofSeq blocks }
