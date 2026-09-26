module PaperReader.Core.FormatsTests

open System
open System.IO
open System.IO.Compression
open System.Text
open Xunit
open PaperReader.Core
open PaperReader.Core.Blocks

// ---- helpers

/// A zip package from (path, text or bytes) entries.
let private package (entries: (string * Choice<string, byte[]>) list) =
    use ms = new MemoryStream()
    do
        use zip = new ZipArchive(ms, ZipArchiveMode.Create, true)
        for name, content in entries do
            let e = zip.CreateEntry name
            use s = e.Open()
            let bytes = match content with Choice1Of2 t -> Encoding.UTF8.GetBytes t | Choice2Of2 b -> b
            s.Write(bytes, 0, bytes.Length)
    ms.ToArray()

let private text (s: string) = Choice1Of2 s
let private binary (b: byte[]) = Choice2Of2 b

/// A PNG header of the given size (enough for imageSize; the pixels don't matter to the readers).
let private pngOf (w: int) (h: int) =
    let b = Array.zeroCreate<byte> 64
    Array.blit [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |] 0 b 0 8
    let be (v: int) = [| byte (v >>> 24); byte (v >>> 16); byte (v >>> 8); byte v |]
    Array.blit (be w) 0 b 16 4
    Array.blit (be h) 0 b 20 4
    b

let private texts (blocks: Block list) =
    blocks |> List.map (function
        | Block.Heading (l, t) -> sprintf "H%d %s" l t
        | Block.Paragraph t -> "P " + t
        | Block.Item t -> "- " + t
        | Block.Math (l, n) -> sprintf "M %s %A" l n
        | Block.Image (_, alt, c) -> sprintf "IMG %s|%s" alt c
        | Block.Table (rows, c) -> sprintf "TAB %d %s" rows.Length c
        | Block.Code (t, _) -> "CODE " + t
        | Block.Break -> "BREAK")

// ---- LaTeX read aloud

[<Fact>]
let ``LaTeX is read the way a lecturer says it`` () =
    Assert.Equal("x sub i squared", MathText.speakLatex "x_i^2")
    Assert.Equal("a over b", MathText.speakLatex @"\frac {a} {b}")
    Assert.Equal("the sum over i equals 1 to n of x sub i", MathText.speakLatex @"\sum _{i=1}^{n} x_{i}")
    Assert.Equal("the square root of d sub k", MathText.speakLatex @"\sqrt{d_k}")
    Assert.Equal("alpha is at most beta minus 1", MathText.speakLatex @"\alpha \leq \beta - 1")
    Assert.Equal("x hat", MathText.speakLatex @"\hat{x}")
    Assert.Equal("q transpose k", MathText.speakLatex @"q^\top k")
    // unknown commands never throw
    Assert.Equal("foo x", MathText.speakLatex @"\foo x")

// ---- detection

[<Fact>]
let ``formats are told apart by content, then by name`` () =
    let d name (bytes: byte[]) = Formats.detect name bytes
    Assert.Equal(Some Formats.Format.Pdf, d "x.bin" (Encoding.ASCII.GetBytes "%PDF-1.7\n..."))
    Assert.Equal(Some Formats.Format.Html, d "page" (Encoding.UTF8.GetBytes "<!DOCTYPE html><html><body><p>Hi</p></body></html>"))
    Assert.Equal(Some Formats.Format.Markdown, d "notes.md" (Encoding.UTF8.GetBytes "Hello"))
    Assert.Equal(Some Formats.Format.Markdown, d "notes" (Encoding.UTF8.GetBytes "# Title\n\nText with a [link](http://x)."))
    Assert.Equal(Some Formats.Format.Text, d "story.txt" (Encoding.UTF8.GetBytes "Once upon a time."))
    Assert.Equal(Some(Formats.Format.Image "image/png"), d "scan" (pngOf 800 600))
    Assert.Equal(None, d "program.exe" [| 0x4Duy; 0x5Auy; 0uy; 0uy; 1uy |])
    let epub = package [ "mimetype", text "application/epub+zip"; "META-INF/container.xml", text "<container/>" ]
    Assert.Equal(Some Formats.Format.Epub, d "book.zip" epub)
    Assert.Equal(Some Formats.Format.Docx, d "a" (package [ "word/document.xml", text "<w:document/>" ]))
    Assert.Equal(Some Formats.Format.Pptx, d "a" (package [ "ppt/presentation.xml", text "<p:presentation/>" ]))

[<Fact>]
let ``a shared link stands for the document it points to`` () =
    Assert.Equal(Some "https://example.org/a", Formats.addressIn (Encoding.UTF8.GetBytes "https://example.org/a\n"))
    Assert.Equal(Some "https://example.org/a", Formats.addressIn (Encoding.UTF8.GetBytes "Great article https://example.org/a"))
    Assert.Equal(None, Formats.addressIn (Encoding.UTF8.GetBytes "A paragraph of text that mentions https://example.org/a in passing, and goes on and on.\nMore.\nMore.\nMore."))

[<Fact>]
let ``a downloaded web page keeps its address for its pictures`` () =
    let html = Encoding.UTF8.GetBytes "<html><body><p>Hi</p><img src=\"fig.png\"></body></html>"
    match Formats.downloaded "https://example.org/blog/post" html with
    | Some (bytes, name) ->
        Assert.Equal("post.html", name)
        let doc = Markup.webPage (Encoding.UTF8.GetString bytes) "x"
        Assert.Contains(doc.Blocks, fun b -> b = Block.Image(Link "https://example.org/blog/fig.png", "", ""))
    | None -> failwith "not recognised"

// ---- HTML

[<Fact>]
let ``a web page is read without its menus, with its math, figures and tables`` () =
    let html = """<html><head><title>Softmax - Wikipedia</title></head><body>
<nav><ul><li><a href="/">Home</a></li><li><a href="/b">B</a></li></ul></nav>
<div class="mw-body"><h1>Softmax</h1>
<ul class="langs"><li><a href="/de">Deutsch</a></li><li><a href="/fr">Français</a></li><li><a href="/fi">Suomi</a></li></ul>
<p>The softmax of <span style="display:none"><math alttext="{\displaystyle z_{i}}"><annotation encoding="application/x-tex">{\displaystyle z_{i}</annotation></math> stray</span><img class="mwe-math-fallback-image-inline" alt="z_i" src="x.svg"> is positive.<sup class="reference"><a href="#c1">[1]</a></sup> It sums to 1.</p>
<p><math display="block" alttext="\sigma(z)_i = \frac{e^{z_i}}{\sum_j e^{z_j}}"></math></p>
<figure><img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAMgAAABkCAIAAABM5OhcAAAA" alt="plot"><figcaption>Figure 2: The function.</figcaption></figure>
<table><caption>Table 1: Values.</caption><tr><th>z</th><th>p</th></tr><tr><td>1</td><td>0.1</td></tr></table>
<h2>See also</h2><ul><li><a href="/x">Other</a></li></ul>
</div><footer class="site-footer"><p>Copyright</p></footer></body></html>"""
    let doc = Markup.webPage html "fallback"
    Assert.Equal("Softmax", doc.Title)
    let doc = { doc with Blocks = doc.Blocks |> Markup.dropReferences }
    let t = texts doc.Blocks
    Assert.DoesNotContain(t, fun l -> l.Contains "Home" || l.Contains "Deutsch" || l.Contains "Copyright" || l.Contains "Other" || l.Contains "stray")
    Assert.Contains("P The softmax of $z_{i}$ is positive. It sums to 1.", t)
    Assert.Contains(t, fun l -> l.StartsWith @"M \sigma(z)_i")
    Assert.Contains(t, fun l -> l = "IMG plot|Figure 2: The function.")
    Assert.Contains("TAB 2 Table 1: Values.", t)

[<Fact>]
let ``numbered equations laid out as tables keep their numbers`` () =
    let html = """<html><body><article><p>Some text long enough to be an article about attention and what it computes.</p>
<table class="ltx_equation"><tr><td><math alttext="a = b" display="block"></math></td><td class="ltx_eqn_eqno">(3)</td></tr></table></article></body></html>"""
    let doc = Markup.webPage html "x"
    Assert.Contains(Block.Math("a = b", Some "3"), doc.Blocks)

// ---- Markdown and text

[<Fact>]
let ``Markdown headings, lists, math, tables, code and pictures become blocks`` () =
    let md = "# Title\n\nSome *emphasis* and a [link](http://x) with $x^2$.\n\n- one\n- two\n\n$$\na + b\n$$\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```\nprint(1)\n```\n\n![a cat](img-0.jpeg)\n"
    let resolve (src: string) = if src = "img-0.jpeg" then Some(Data(pngOf 300 200)) else None
    let t = texts (Markup.markdownBlocks md resolve)
    Assert.Equal<string list>(
        [ "H1 Title"; "P Some emphasis and a link with $x^2$."; "- one"; "- two"; "M a + b None"; "TAB 2 "; "CODE print(1)"; "IMG a cat|" ], t)

[<Fact>]
let ``plain text finds chapters and leaves out the Project Gutenberg licence`` () =
    let txt = "The Project Gutenberg eBook\n*** START OF THE PROJECT GUTENBERG EBOOK X ***\n\nCHAPTER I\n\nIt was a dark and\nstormy night; the rain fell in tor-\nrents.\n\n*** END OF THE PROJECT GUTENBERG EBOOK X ***\nlicence"
    let t = texts (Markup.textBlocks txt)
    Assert.Equal<string list>([ "H1 Chapter I"; "P It was a dark and stormy night; the rain fell in torrents." ], t)

// ---- packages

[<Fact>]
let ``a Word document gives headings, lists, captioned pictures, tables and equations`` () =
    let w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
    let docXml =
        $"""<w:document xmlns:w="{w}" xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"><w:body>
<w:p><w:pPr><w:pStyle w:val="Title"/></w:pPr><w:r><w:t>My Report</w:t></w:r></w:p>
<w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Intro</w:t></w:r></w:p>
<w:p><w:r><w:t xml:space="preserve">Energy is </w:t></w:r><m:oMath><m:r><m:t>E</m:t></m:r><m:r><m:t>=</m:t></m:r><m:sSup><m:e><m:r><m:t>c</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath><w:r><w:t>.</w:t></w:r></w:p>
<w:p><w:pPr><w:numPr><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>First point</w:t></w:r></w:p>
<w:p><w:r><w:drawing><wp:inline><wp:docPr id="1" descr="a chart"/><a:graphic><a:graphicData><a:blip r:embed="rId5"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
<w:p><w:pPr><w:pStyle w:val="Caption"/></w:pPr><w:r><w:t>Figure 1: Sales by year.</w:t></w:r></w:p>
<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Year</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Sales</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>2024</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>10</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
<w:p><m:oMathPara><m:oMath><m:f><m:num><m:r><m:t>a</m:t></m:r></m:num><m:den><m:r><m:t>b</m:t></m:r></m:den></m:f></m:oMath></m:oMathPara></w:p>
</w:body></w:document>"""
    let styles = $"""<w:styles xmlns:w="{w}"><w:style w:styleId="Title"><w:name w:val="Title"/></w:style><w:style w:styleId="Heading1"><w:name w:val="heading 1"/></w:style><w:style w:styleId="Caption"><w:name w:val="caption"/></w:style></w:styles>"""
    let rels = """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId5" Target="media/image1.png"/></Relationships>"""
    let bytes =
        package [ "word/document.xml", text docXml; "word/styles.xml", text styles; "word/_rels/document.xml.rels", text rels
                  "word/media/image1.png", binary (pngOf 400 300) ]
    let doc = Formats.read Formats.Format.Docx bytes "file"
    Assert.Equal("My Report", doc.Title)
    Assert.Equal<string list>(
        [ "H1 Intro"; "P Energy is $E={c}^{2}$."; "- First point"; "IMG a chart|Figure 1: Sales by year."; "TAB 2 "; @"M \frac{a}{b} None" ],
        texts doc.Blocks)

[<Fact>]
let ``slides read in order with titles, bullets, pictures and speaker notes`` () =
    let p = "http://schemas.openxmlformats.org/presentationml/2006/main"
    let a = "http://schemas.openxmlformats.org/drawingml/2006/main"
    let r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
    let rels (items: (string * string) list) =
        let xs = items |> List.map (fun (id, t) -> sprintf "<Relationship Id=\"%s\" Target=\"%s\"/>" id t) |> String.concat ""
        sprintf "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">%s</Relationships>" xs
    let slide (title: string) (bullets: string list) (pic: bool) =
        let ps = bullets |> List.map (fun b -> sprintf "<a:p><a:r><a:t>%s</a:t></a:r></a:p>" b) |> String.concat ""
        let picXml = if pic then """<p:pic><p:nvPicPr><p:cNvPr id="4" name="Picture" descr="diagram"/></p:nvPicPr><p:blipFill><a:blip r:embed="rId2"/></p:blipFill></p:pic>""" else ""
        $"""<p:sld xmlns:p="{p}" xmlns:a="{a}" xmlns:r="{r}"><p:cSld><p:spTree>
<p:sp><p:nvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>{title}</a:t></a:r></a:p></p:txBody></p:sp>
<p:sp><p:nvSpPr><p:nvPr><p:ph idx="1"/></p:nvPr></p:nvSpPr><p:txBody>{ps}</p:txBody></p:sp>{picXml}
<p:sp><p:nvSpPr><p:nvPr><p:ph type="sldNum"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>2</a:t></a:r></a:p></p:txBody></p:sp>
</p:spTree></p:cSld></p:sld>"""
    let notes = $"""<p:notes xmlns:p="{p}" xmlns:a="{a}"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type="body"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Say why this matters.</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>"""
    let pres = $"""<p:presentation xmlns:p="{p}" xmlns:r="{r}"><p:sldIdLst><p:sldId id="256" r:id="rId8"/><p:sldId id="257" r:id="rId9"/></p:sldIdLst></p:presentation>"""
    let bytes =
        package [ "ppt/presentation.xml", text pres
                  "ppt/_rels/presentation.xml.rels", text (rels [ "rId8", "slides/slide1.xml"; "rId9", "slides/slide2.xml" ])
                  "ppt/slides/slide1.xml", text (slide "Welcome" [ "A talk about graphs" ] false)
                  "ppt/slides/slide2.xml", text (slide "Method" [ "Build the graph"; "Walk it" ] true)
                  "ppt/slides/_rels/slide2.xml.rels", text (rels [ "rId2", "../media/image1.png"; "rId3", "../notesSlides/notesSlide2.xml" ])
                  "ppt/media/image1.png", binary (pngOf 640 480)
                  "ppt/notesSlides/notesSlide2.xml", text notes ]
    let doc = Formats.read Formats.Format.Pptx bytes "deck"
    Assert.Equal("Welcome", doc.Title)
    Assert.Equal<string list>(
        [ "BREAK"; "H1 Welcome"; "P A talk about graphs"; "BREAK"; "H1 Method"; "- Build the graph"; "- Walk it"; "IMG diagram|"; "P Say why this matters." ],
        texts doc.Blocks)
    let a, pictures = Blocks.analyze doc
    Assert.Equal(2, a.PageCount)
    Assert.Equal(1, pictures.Length)

[<Fact>]
let ``an EPUB is read chapter by chapter in spine order, with its pictures`` () =
    let opf = """<package xmlns="http://www.idpf.org/2007/opf"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>A Small Book</dc:title></metadata>
<manifest><item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/><item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="img" href="images/map.png" media-type="image/png"/></manifest>
<spine><itemref idref="nav"/><itemref idref="c1"/><itemref idref="c2"/></spine></package>"""
    let chapter (h: string) (body: string) = sprintf """<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>x</title></head><body><h1>%s</h1>%s</body></html>""" h body
    let bytes =
        package [ "mimetype", text "application/epub+zip"
                  "META-INF/container.xml", text """<container><rootfiles><rootfile full-path="OEBPS/content.opf"/></rootfiles></container>"""
                  "OEBPS/content.opf", text opf
                  "OEBPS/nav.xhtml", text (chapter "Contents" "<ol><li><a href='text/ch1.xhtml'>One</a></li></ol>")
                  "OEBPS/text/ch1.xhtml", text (chapter "One" "<p>It begins.</p><p><img src=\"../images/map.png\" alt=\"The map\"/></p>")
                  "OEBPS/text/ch2.xhtml", text (chapter "Two" "<p>It ends.</p>")
                  "OEBPS/images/map.png", binary (pngOf 500 400) ]
    let doc = Formats.read Formats.Format.Epub bytes "book"
    Assert.Equal("A Small Book", doc.Title)
    Assert.Equal<string list>([ "BREAK"; "H1 One"; "P It begins."; "IMG The map|"; "BREAK"; "H1 Two"; "P It ends." ], texts doc.Blocks)

// ---- blocks to units

[<Fact>]
let ``blocks become units and visuals the narrator knows`` () =
    let doc =
        { Title = "Notes"
          Source = "test"
          Blocks =
            [ Block.Heading(1, "Notes")
              Block.Heading(2, "2.1 Method")
              Block.Paragraph "We minimise $\\sum_i x_i^2$ here. Then we stop."
              Block.Math(@"a = b \tag{4}", None)
              Block.Image(Data(pngOf 300 300), "", "Figure 3: A plot.")
              Block.Table([ [ "k"; "v" ]; [ "1"; "2" ] ], "")
              Block.Paragraph "As Figure 3 and Equation 4 show, it works." ] }
    let a, pictures = Blocks.analyze doc
    let units = a.Units |> Array.map (fun u -> u.Kind, u.Text, u.Visual) |> List.ofArray
    // the title is read once
    Assert.Equal(1, a.Units |> Array.filter (fun u -> u.Text = "Notes") |> Array.length)
    Assert.Equal("Section 2.1. Method.", a.Units.[1].Spoken)
    Assert.Contains((UnitKind.Sentence, "We minimise $\\sum_i x_i^2$ here.", Some "S2"), units)
    Assert.Contains((UnitKind.Sentence, "Then we stop.", None), units)
    Assert.Equal("the sum over i of x sub i squared", (a.Units |> Array.find (fun u -> u.Id = "S2")).Spoken.Replace("We minimise ", "").Replace(" here.", ""))
    let eq = a.Visuals |> Array.find (fun v -> v.Kind = VisualKind.Equation)
    Assert.Equal(Some "4", eq.EqNumber)
    Assert.Equal(Some "a = b", eq.Latex)
    Assert.Contains(a.Visuals, fun v -> v.Id = "Fig3" && v.Kind = VisualKind.Figure)
    Assert.Contains(a.Visuals, fun v -> v.Kind = VisualKind.Table && v.EqNumber = None)
    Assert.Equal(a.Visuals.Length, pictures.Length)
    // references resolve to the visuals by number
    let script = Narration.buildLocal a
    let last = script.Segments |> Array.last
    Assert.True(last.Show = Some eq.Id || last.Show = Some "Fig3")
    // the text kept for questions names the figure by id
    Assert.Contains("[Fig3: Figure 3: A plot.]", Blocks.toMarkdown doc a)

[<Fact>]
let ``icons and unreadable pictures are left out, their captions kept`` () =
    let doc =
        { Title = "T"; Source = "test"
          Blocks = [ Block.Image(Data(pngOf 16 16), "", ""); Block.Image(Data [| 1uy; 2uy |], "", "Figure 1: Kept."); Block.Image(Data(pngOf 200 100), "", "") ] }
    let resolved = (Blocks.resolveImages (fun _ -> Threading.Tasks.Task.FromResult None) doc).Result
    Assert.Equal<string list>([ "P Figure 1: Kept."; "IMG |" ], texts resolved.Blocks)

[<Fact>]
let ``captions next to pictures and tables are attached to them`` () =
    let blocks =
        [ Block.Paragraph "Table 2: Scores."; Block.Table([ [ "a" ] ], "")
          Block.Image(Data(pngOf 100 100), "", ""); Block.Paragraph "Figure 5. A cat." ]
    Assert.Equal<string list>([ "TAB 1 Table 2: Scores."; "IMG |Figure 5. A cat." ], texts (Blocks.attachCaptions blocks))

[<Fact>]
let ``a Markdown picture whose alt text is a caption is captioned by it`` () =
    let resolve (_: string) = Some(Data(pngOf 300 200))
    Assert.Equal<string list>([ "IMG |Figure 1: Weights." ], texts (Markup.markdownBlocks "![Figure 1: Weights.](x.png)" resolve))
