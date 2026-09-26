/// The kinds of document the app reads, telling them apart, and reading those that aren't PDFs (the PDF
/// layout analysis is Layout) into blocks.
module PaperReader.Core.Formats

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open PaperReader.Core.Blocks

[<RequireQualifiedAccess>]
type Format =
    | Pdf
    | Epub
    | Docx
    | Odt
    | Pptx
    | Html
    | Markdown
    | Text
    /// A photo or scan of pages, read by Mistral OCR.
    | Image of mime: string

/// The name stored with a paper (PaperInfo.Format).
let key =
    function
    | Format.Pdf -> "pdf"
    | Format.Epub -> "epub"
    | Format.Docx -> "docx"
    | Format.Odt -> "odt"
    | Format.Pptx -> "pptx"
    | Format.Html -> "html"
    | Format.Markdown -> "md"
    | Format.Text -> "txt"
    | Format.Image _ -> "image"

/// File extension the document is kept under.
let extension (f: Format) =
    match f with
    | Format.Image mime -> "." + (match mime with "image/jpeg" -> "jpg" | m -> m.Substring(m.IndexOf '/' + 1))
    | f -> "." + key f

/// What a "page" of the document is called: a slide, a chapter, or a page (for documents without pages of
/// their own, about 3000 characters of text).
let pageNoun (formatKey: string) =
    match formatKey with
    | "pptx" -> "slide"
    | "epub" -> "chapter"
    | _ -> "page"

/// Name of the format for people ("EPUB book").
let describe (formatKey: string) =
    match formatKey with
    | "pdf" -> "PDF"
    | "epub" -> "EPUB book"
    | "docx" -> "Word document"
    | "odt" -> "OpenDocument text"
    | "pptx" -> "Slides"
    | "html" -> "Web page"
    | "md" -> "Markdown"
    | "txt" -> "Text"
    | "image" -> "Scanned pages"
    | k -> k.ToUpperInvariant()

/// File name patterns for the file picker.
let patterns =
    [| "*.pdf"; "*.epub"; "*.docx"; "*.odt"; "*.pptx"; "*.html"; "*.htm"; "*.xhtml"; "*.md"; "*.markdown"; "*.txt"
       "*.png"; "*.jpg"; "*.jpeg"; "*.webp" |]

/// Media types the app opens (file picker, "Open with" and "Share" on Android, the desktop's "Open with").
let mimeTypes =
    [| "application/pdf"; "application/epub+zip"
       "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
       "application/vnd.oasis.opendocument.text"
       "application/vnd.openxmlformats-officedocument.presentationml.presentation"
       "text/html"; "application/xhtml+xml"; "text/markdown"; "text/x-markdown"; "text/plain"
       "image/png"; "image/jpeg"; "image/webp" |]

let private startsWith (b: byte[]) (sig': byte[]) = b.Length >= sig'.Length && Array.forall2 (=) sig' b.[.. sig'.Length - 1]

let private imageMime (b: byte[]) =
    if startsWith b [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |] then Some "image/png"
    elif startsWith b [| 0xFFuy; 0xD8uy; 0xFFuy |] then Some "image/jpeg"
    elif b.Length > 12 && Encoding.ASCII.GetString(b, 0, 4) = "RIFF" && Encoding.ASCII.GetString(b, 8, 4) = "WEBP" then Some "image/webp"
    else None

/// What a file is, from its content first and its name second. None when it isn't something the app reads.
let detect (fileName: string) (bytes: byte[]) : Format option =
    let ext = Path.GetExtension(fileName).ToLowerInvariant()
    let head = Encoding.ASCII.GetString(bytes, 0, min bytes.Length 1024)
    if head.Contains "%PDF-" then Some Format.Pdf
    elif startsWith bytes [| 0x50uy; 0x4Buy; 0x03uy; 0x04uy |] then
        try
            use pkg = new Packages.Package(bytes)
            let mimetype = pkg.Text "mimetype" |> Option.map (fun s -> s.Trim()) |> Option.defaultValue ""
            if mimetype = "application/epub+zip" || pkg.Has "META-INF/container.xml" then Some Format.Epub
            elif pkg.Has "word/document.xml" then Some Format.Docx
            elif pkg.Has "ppt/presentation.xml" then Some Format.Pptx
            elif mimetype = "application/vnd.oasis.opendocument.text" || (pkg.Has "content.xml" && ext = ".odt") then Some Format.Odt
            else None
        with _ -> None
    else
        match imageMime bytes with
        | Some mime -> Some(Format.Image mime)
        | None ->
            // text: nothing binary in the first few kilobytes
            let n = min bytes.Length 8192
            let binary = bytes |> Seq.take n |> Seq.exists (fun b -> b = 0uy)
            let utf16 = bytes.Length >= 2 && ((bytes.[0] = 0xFFuy && bytes.[1] = 0xFEuy) || (bytes.[0] = 0xFEuy && bytes.[1] = 0xFFuy))
            if binary && not utf16 then None
            else
                let text = Packages.decodeText bytes.[.. n - 1]
                let lower = text.TrimStart().ToLowerInvariant()
                if ext = ".html" || ext = ".htm" || ext = ".xhtml"
                   || lower.StartsWith "<!doctype html" || lower.StartsWith "<html"
                   || (lower.StartsWith "<?xml" && lower.Contains "<html")
                   || (ext <> ".md" && ext <> ".txt" && Regex.IsMatch(lower, @"<(body|p|div|article)[\s>]")) then Some Format.Html
                elif ext = ".md" || ext = ".markdown" || ext = ".mdown" || ext = ".mkd" then Some Format.Markdown
                elif ext = ".txt" || ext = ".text" then Some Format.Text
                elif Regex.IsMatch(text, @"^#{1,6}\s+\S|^```|\]\([^)]+\)|^\s*[-*]\s+\S", RegexOptions.Multiline) then Some Format.Markdown
                elif ext = "" || ext = ".rst" || ext = ".org" || ext = ".tex" || ext = ".log" || ext = ".csv" then Some Format.Text
                else None

/// Why a file can't be opened, for people.
let unsupported (fileName: string) =
    let ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant()
    sprintf "%s isn't a kind of document Paper Reader can read. It reads PDF, EPUB, Word (DOCX), OpenDocument (ODT), PowerPoint (PPTX), web pages (HTML), Markdown and text files, and with a Mistral key photos of pages (PNG, JPEG, WebP)."
        (if ext = "" then "This file" else sprintf "A .%s file" (ext.ToLowerInvariant()))

/// A document from Mistral OCR's pages (a photo or scan): its markdown, with the pictures OCR cut out.
let fromOcr (pages: Mistral.OcrPage list) (fallbackTitle: string) (source: string) : Document =
    let blocks =
        [ for page in pages |> List.sortBy (fun p -> p.Index) do
              let images = page.Images |> dict
              let resolve (src: string) = match images.TryGetValue src with | true, b -> Some(Data b) | _ -> None
              yield Block.Break
              yield! Markup.markdownBlocks page.Markdown resolve ]
    let title =
        blocks |> List.tryPick (function Block.Heading (_, t) when t.Length < 200 -> Some t | _ -> None)
        |> Option.defaultValue fallbackTitle
    { Title = title; Source = source; Blocks = blocks }

/// Reads a document that isn't a PDF or an image (those need Layout or OCR). `fallbackTitle` is used when the
/// document names no title of its own (the file name).
let read (format: Format) (bytes: byte[]) (fallbackTitle: string) : Document =
    let doc =
        match format with
        | Format.Epub ->
            use pkg = new Packages.Package(bytes)
            Packages.epub pkg fallbackTitle
        | Format.Docx ->
            use pkg = new Packages.Package(bytes)
            Packages.docx pkg fallbackTitle
        | Format.Odt ->
            use pkg = new Packages.Package(bytes)
            Packages.odt pkg fallbackTitle
        | Format.Pptx ->
            use pkg = new Packages.Package(bytes)
            Packages.pptx pkg fallbackTitle
        | Format.Html -> Markup.webPage (Packages.decodeText bytes) fallbackTitle
        | Format.Markdown ->
            let blocks = Markup.markdownBlocks (Packages.decodeText bytes) (fun src -> if src.StartsWith "data:" then Markup.dataUri src |> Option.map Data else None)
            let title = blocks |> List.tryPick (function Block.Heading (1, t) -> Some t | _ -> None) |> Option.defaultValue fallbackTitle
            { Title = title; Source = "a Markdown text"; Blocks = blocks }
        | Format.Text ->
            let blocks = Markup.textBlocks (Packages.decodeText bytes)
            // a first line standing alone is the title
            let title, blocks =
                match blocks with
                | Block.Heading (_, t) :: rest -> t, rest
                | Block.Paragraph t :: rest when t.Length <= 100 && not (t.EndsWith ".") -> t, rest
                | _ -> fallbackTitle, blocks
            { Title = title; Source = "a plain text"; Blocks = blocks }
        | Format.Pdf | Format.Image _ -> invalidArg "format" "PDFs and images are read by the layout analysis or OCR"
    { doc with Blocks = doc.Blocks |> Markup.dropReferences |> attachCaptions }

let private http =
    let h = new HttpClient(Timeout = TimeSpan.FromSeconds 20.0)
    h.DefaultRequestHeaders.UserAgent.ParseAdd "Mozilla/5.0 (compatible; PaperReader/1.0)"
    h

let private maxImageBytes = 15L * 1024L * 1024L

/// Downloads a web page's picture; None when it can't be had (offline, refused, too large, not allowed across
/// sites in the browser).
let fetchImage (ct: CancellationToken) (url: string) : Task<byte[] option> =
    task {
        try
            use! resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            if not resp.IsSuccessStatusCode then return None
            elif resp.Content.Headers.ContentLength.HasValue && resp.Content.Headers.ContentLength.Value > maxImageBytes then return None
            else
                let! bytes = resp.Content.ReadAsByteArrayAsync(ct)
                return Some bytes
        with
        | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException())
        | _ -> return None
    }

let private addressRx = Regex(@"^https?://\S+$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// The web address a small text consists of (a link shared from a browser, a pasted address), if it is one.
let addressIn (bytes: byte[]) : string option =
    if bytes.Length = 0 || bytes.Length > 4096 then None
    else
        let t = (Packages.decodeText bytes).Trim()
        // "Title https://…" is how some apps share a link
        let last = t.Split([| ' '; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.defaultValue ""
        if addressRx.IsMatch t then Some t
        elif addressRx.IsMatch last && t.Split('\n').Length <= 3 then Some last
        else None

/// A downloaded document ready to import: its bytes and a file name with the right extension. A web page gets
/// a <base href> so its pictures can be fetched later. None when it isn't a kind of document the app reads.
let downloaded (url: string) (bytes: byte[]) : (byte[] * string) option =
    let uri = Uri url
    let fromPath = try Uri.UnescapeDataString(Path.GetFileName uri.AbsolutePath) with _ -> ""
    let stem =
        let s = Path.GetFileNameWithoutExtension fromPath
        let s = Regex.Replace((if s = "" then uri.Host else s), @"[^\p{L}\p{N} \-_.,]", "").Trim()
        if s.Length > 80 then s.Substring(0, 80) else s
    match detect fromPath bytes with
    | None -> None
    | Some Format.Html ->
        let tag = Encoding.UTF8.GetBytes(sprintf "<base href=\"%s\">\n" (Net.WebUtility.HtmlEncode url))
        Some(Array.append tag bytes, stem + ".html")
    | Some f -> Some(bytes, stem + extension f)

let private maxDocumentBytes = 150L * 1024L * 1024L

/// Downloads a document from the web: a PDF, a web page, an EPUB, ... Fails with a message for people.
let fetchDocument (url: string) (ct: CancellationToken) : Task<byte[] * string> =
    task {
        use req = new HttpRequestMessage(HttpMethod.Get, url)
        req.Headers.Accept.ParseAdd "text/html,application/xhtml+xml,application/pdf,*/*;q=0.8"
        use cts = CancellationTokenSource.CreateLinkedTokenSource ct
        cts.CancelAfter(TimeSpan.FromSeconds 90.0)
        let! resp =
            task {
                try return! http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException())
                | :? OperationCanceledException -> return failwithf "%s took too long to answer." (Uri url).Host
                | :? HttpRequestException -> return failwithf "Couldn't connect to %s." (Uri url).Host
            }
        use resp = resp
        if not resp.IsSuccessStatusCode then return failwithf "%s refused the download (%d)." (Uri url).Host (int resp.StatusCode)
        elif resp.Content.Headers.ContentLength.HasValue && resp.Content.Headers.ContentLength.Value > maxDocumentBytes then
            return failwith "The document is too large to download."
        else
            let! bytes = resp.Content.ReadAsByteArrayAsync(cts.Token)
            // redirects end where the document is: its address resolves the page's pictures
            let final = if isNull resp.RequestMessage || isNull resp.RequestMessage.RequestUri then url else resp.RequestMessage.RequestUri.AbsoluteUri
            match downloaded final bytes with
            | Some d -> return d
            | None -> return failwithf "%s sent something Paper Reader can't read." (Uri url).Host
    }
