module PaperReader.Core.CoreTests

open System
open System.IO
open Xunit
open PaperReader.Core

// ---- WAV

let private wav (encoding: int16) (bits: int16) (rate: int) (data: byte[]) (dataSizeField: uint32) =
    use ms = new MemoryStream()
    use w = new BinaryWriter(ms)
    w.Write(Text.Encoding.ASCII.GetBytes "RIFF"); w.Write(36 + data.Length); w.Write(Text.Encoding.ASCII.GetBytes "WAVE")
    w.Write(Text.Encoding.ASCII.GetBytes "fmt "); w.Write 16; w.Write encoding; w.Write 1s; w.Write rate
    w.Write(rate * int bits / 8); w.Write(int16 (bits / 8s)); w.Write bits
    w.Write(Text.Encoding.ASCII.GetBytes "data"); w.Write dataSizeField; w.Write data
    ms.ToArray()

[<Fact>]
let ``16-bit clip gets the requested pause appended and a correct duration`` () =
    let pcm = Array.create (24000 * 2) 7uy // one second at 24 kHz
    let path = Path.GetTempFileName()
    let ms = Wav.normalizeTo (wav 1s 16s 24000 pcm (uint32 pcm.Length)) 500 path
    Assert.Equal(1500, ms)
    Assert.Equal(1500, Wav.durationMs path)
    let f, data = Wav.parse (File.ReadAllBytes path)
    Assert.Equal(24000, f.SampleRate)
    Assert.Equal(7uy, data.Array.[data.Offset])
    Assert.Equal(0uy, data.Array.[data.Offset + data.Count - 1])

[<Fact>]
let ``streaming header with unknown data size is read to the end`` () =
    let pcm = Array.create 4800 1uy
    let path = Path.GetTempFileName()
    let ms = Wav.normalizeTo (wav 1s 16s 24000 pcm UInt32.MaxValue) 0 path
    Assert.Equal(100, ms)

[<Fact>]
let ``float samples are converted to 16-bit`` () =
    let samples = [| 0.5f; -1.0f; 2.0f |] |> Array.collect BitConverter.GetBytes
    let path = Path.GetTempFileName()
    Wav.normalizeTo (wav 3s 32s 16000 samples (uint32 samples.Length)) 0 path |> ignore
    let f, data = Wav.parse (File.ReadAllBytes path)
    Assert.Equal(16, f.BitsPerSample)
    let s i = BitConverter.ToInt16(data.Array, data.Offset + i * 2)
    Assert.Equal(16383s, s 0)
    Assert.Equal(-32767s, s 1)
    Assert.Equal(32767s, s 2) // clipped

// ---- seeking across clips

let private durations = Map [ 0, 4000; 1, 10000; 2, 3000 ]
let private dur i = durations.TryFind i

[<Fact>]
let ``back 15 seconds crosses into earlier clips`` () =
    let p = Timeline.back dur 15000 { Segment = 2; OffsetMs = 2000 }
    // 2s in clip 2, 10s of clip 1, leaves 3s to take from the 4s clip 0
    Assert.Equal({ Timeline.Segment = 0; Timeline.OffsetMs = 1000 }, p)

[<Fact>]
let ``back stops at the start of the paper`` () =
    Assert.Equal({ Timeline.Segment = 0; Timeline.OffsetMs = 0 }, Timeline.back dur 60000 { Segment = 1; OffsetMs = 500 })

[<Fact>]
let ``back within a clip`` () =
    Assert.Equal({ Timeline.Segment = 1; Timeline.OffsetMs = 1000 }, Timeline.back dur 5000 { Segment = 1; OffsetMs = 6000 })

[<Fact>]
let ``forward enters a clip that has no audio yet at its start`` () =
    let p = Timeline.forward dur 4 15000 { Segment = 1; OffsetMs = 0 }
    // 10s left in clip 1, 3s in clip 2, clip 3 is unknown
    Assert.Equal({ Timeline.Segment = 3; Timeline.OffsetMs = 0 }, p)

[<Fact>]
let ``forward within known clips`` () =
    Assert.Equal({ Timeline.Segment = 1; Timeline.OffsetMs = 3000 }, Timeline.forward dur 3 7000 { Segment = 0; OffsetMs = 0 })

// ---- speech text

[<Fact>]
let ``citations and abbreviations are cleaned for listening`` () =
    let s = SpeechText.forSpeech "Deep nets [12, 14] work well (He et al., 2016), e.g. ResNets; see Fig. 3 and Eq. 2."
    Assert.Equal("Deep nets work well, for example, ResNets; see Figure 3 and Equation 2.", s)

[<Fact>]
let ``sentence ends are not found after abbreviations or initials`` () =
    Assert.False(SpeechText.endsSentence "e.g." "The")
    Assert.False(SpeechText.endsSentence "J." "Smith")
    Assert.True(SpeechText.endsSentence "results." "We")
    Assert.False(SpeechText.endsSentence "(Fig." "2).")

[<Fact>]
let ``math glyphs are spoken`` () =
    Assert.Equal(" alpha ", MathText.speakGlyph "α")
    Assert.Equal(" is at most ", MathText.speakGlyph "≤")
    Assert.Equal(" squared ", MathText.speakSuperscript "2")
    Assert.True(MathText.isMathFont "ABCDEF+CMMI10")
    Assert.False(MathText.isMathFont "ABCDEF+CMR10")

// ---- cache round trip

[<Fact>]
let ``script survives a save and load`` () =
    let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    let paths = Store.Paths root
    Directory.CreateDirectory(paths.Paper "abc") |> ignore
    let visual = { Id = "E3"; Kind = VisualKind.Algorithm; Parts = [| { Page = 1; X = 1.5; Y = 2.0; W = 30.0; H = 4.0 }; { Page = 1; X = 5.0; Y = 9.0; W = 10.0; H = 3.0 } |]; EqNumber = Some "1"; RawText = "x_{i}"; Latex = Some @"x_{i}" }
    let script =
        { Version = Script.currentVersion; Title = "T"; PageCount = 2; Narrator = "offline"
          Sections = [| { Title = "T"; FirstSegment = 0 } |]
          Segments = [| { Index = 0; Kind = UnitKind.Equation; Say = "Algorithm 1."; Show = Some "E3"; Reason = ShowReason.Recent; Section = 0; Page = 1; PauseAfterMs = 900 } |]
          Visuals = [| visual |] }
    Store.saveScript paths "abc" script
    let loaded = (Store.loadScript paths "abc").Value
    Assert.Equal<Segment[]>(script.Segments, loaded.Segments)
    Assert.Equal<Visual[]>(script.Visuals, loaded.Visuals)
    Directory.Delete(root, true)

// ---- narration

[<Fact>]
let ``an equation stays on screen for the sentences that explain it`` () =
    let unit id kind vis = { Id = id; Kind = kind; Text = id; Spoken = "say " + id; Visual = vis; Page = 0; Section = 1; ParagraphEnd = false }
    let v = { Id = "E1"; Kind = VisualKind.Equation; Parts = [| { Page = 0; X = 0.0; Y = 0.0; W = 1.0; H = 1.0 } |]; EqNumber = Some "4"; RawText = ""; Latex = None }
    let a =
        { Title = "T"; PageCount = 1; Sections = [| "T"; "S" |]; Visuals = [| v |]
          Units = [| unit "E1" UnitKind.Equation (Some "E1"); unit "S1" UnitKind.Sentence None; unit "S2" UnitKind.Sentence None
                     unit "S3" UnitKind.Sentence None; unit "S4" UnitKind.Sentence None; { unit "S5" UnitKind.Sentence None with Spoken = "As Equation 4 shows." } |] }
    let s = Narration.buildLocal a
    let shown = s.Segments |> Array.map (fun g -> g.Show, g.Reason)
    Assert.Equal((Some "E1", ShowReason.Own), shown.[0])
    Assert.Equal((Some "E1", ShowReason.Recent), shown.[3])
    Assert.Equal((None, ShowReason.Own), shown.[4])
    Assert.Equal((Some "E1", ShowReason.Reference), shown.[5])

// ---- stopping at equations

let private seg i show reason =
    { Index = i; Kind = UnitKind.Sentence; Say = "s"; Show = show; Reason = reason; Section = 1; Page = 0; PauseAfterMs = 0 }

let private scriptWith (segments: Segment[]) (visuals: Visual[]) =
    { Version = Script.currentVersion; Title = "T"; PageCount = 1; Narrator = "offline"
      Sections = [| { Title = "T"; FirstSegment = 0 } |]; Segments = segments; Visuals = visuals }

let private eq id = { Id = id; Kind = VisualKind.Equation; Parts = [| { Page = 0; X = 0.0; Y = 0.0; W = 10.0; H = 10.0 } |]; EqNumber = None; RawText = ""; Latex = None }

[<Fact>]
let ``the reader stops after the last sentence that explains an equation, not after the ones that only keep it up`` () =
    let s =
        scriptWith
            [| seg 0 None ShowReason.Own
               seg 1 (Some "E1") ShowReason.Own          // the equation is read
               seg 2 (Some "E1") ShowReason.Reference    // "where x is ..."
               seg 3 (Some "E1") ShowReason.Recent       // only kept on screen
               seg 4 None ShowReason.Own
               seg 5 (Some "E1") ShowReason.Reference    // referred to again later: no second stop
               seg 6 (Some "E2") ShowReason.Own |]
            [| eq "E1"; eq "E2" |]
    Assert.Equal<Map<int, string>>(Map.ofList [ 2, "E1"; 6, "E2" ], Narration.equationStops s)
    Assert.Equal<(string * int)[]>([| "E1", 1; "E2", 6 |], Narration.equationOrder s |> Array.map (fun (v, i) -> v.Id, i))

[<Fact>]
let ``inline math never stops the reader`` () =
    let inlineVisual = { eq "S1" with Kind = VisualKind.Inline }
    let s = scriptWith [| seg 0 (Some "S1") ShowReason.Own |] [| inlineVisual |]
    Assert.True((Narration.equationStops s).IsEmpty)

// ---- OCR

[<Fact>]
let ``math is taken out of OCR markdown without delimiters or tags`` () =
    let md = "We have $x_i$ and\n$$\\frac{1}{\\sqrt{d_k}} \\tag{2}$$\nand \\[ a = b \\] also \\(c\\)."
    Assert.Equal<string list>([ "x_i"; @"\frac{1}{\sqrt{d_k}}"; "a = b"; "c" ], Ocr.mathPieces md)

[<Fact>]
let ``the OCR piece that matches the extracted text is chosen`` () =
    let pieces = [ @"\alpha = 0.1"; @"\mathrm{Attention}(Q, K, V) = \mathrm{softmax}(QK^T)V" ]
    Assert.Equal<string list>([ pieces.[1] ], Ocr.bestPieces "Attention(Q,K,V) = softmax(QKT)V" pieces)

[<Fact>]
let ``OCR grows a display equation that was cut short and records its LaTeX`` () =
    // page 600 x 800 pt, OCR image 1200 x 1600 px (2 px per point)
    let v = { eq "E1" with Parts = [| { Page = 0; X = 100.0; Y = 200.0; W = 150.0; H = 20.0 } |] }
    let block = { Mistral.X0 = 190.0; Mistral.Y0 = 396.0; Mistral.X1 = 560.0; Mistral.Y1 = 444.0; Mistral.Kind = "equation"; Mistral.Content = "$$y = f(x) + g(x)$$" }
    let page = { Mistral.Index = 0; Mistral.Width = 1200.0; Mistral.Height = 1600.0; Mistral.Markdown = ""; Mistral.Blocks = [ block ] }
    let refined = (Ocr.refine [ page ] [| 600.0, 800.0 |] [| v |]).[0]
    let r = refined.Parts.[0]
    Assert.Equal(95.0, r.X, 3)
    Assert.Equal(198.0, r.Y, 3)
    Assert.Equal(280.0, r.X + r.W, 3)
    Assert.Equal(222.0, r.Y + r.H, 3)
    Assert.Equal(Some "y = f(x) + g(x)", refined.Latex)

[<Fact>]
let ``an OCR block elsewhere on the page leaves the equation alone`` () =
    let v = { eq "E1" with Parts = [| { Page = 0; X = 100.0; Y = 200.0; W = 150.0; H = 20.0 } |] }
    let block = { Mistral.X0 = 100.0; Mistral.Y0 = 1000.0; Mistral.X1 = 400.0; Mistral.Y1 = 1040.0; Mistral.Kind = "equation"; Mistral.Content = "$$z$$" }
    let page = { Mistral.Index = 0; Mistral.Width = 1200.0; Mistral.Height = 1600.0; Mistral.Markdown = ""; Mistral.Blocks = [ block ] }
    let refined = (Ocr.refine [ page ] [| 600.0, 800.0 |] [| v |]).[0]
    Assert.Equal<PageRect[]>(v.Parts, refined.Parts)
    Assert.Equal(None, refined.Latex)

// ---- crop cleanup

[<Fact>]
let ``a glyph running into the right edge counts as cut off`` () =
    let white, black = 0xFFFFFFFF |> int, 0xFF000000 |> int
    let w, h = 40, 20
    let px = Array.create (w * h) white
    for y in 5 .. 14 do
        for x in 10 .. 39 do px.[y * w + x] <- black // a thick bar reaching the right edge
    CropTidy.clean px w h false
    Assert.Equal((false, false, true, false), CropTidy.clipped px w h)

// ---- figures and tables

let private block kind x0 y0 x1 y1 content =
    { Mistral.X0 = x0; Mistral.Y0 = y0; Mistral.X1 = x1; Mistral.Y1 = y1; Mistral.Kind = kind; Mistral.Content = content }

[<Fact>]
let ``OCR images, sub-captions and tables are paired with their numbered captions`` () =
    // page image 600 x 800 px for a 600 x 800 pt page: 1 px per point
    let page =
        { Mistral.Index = 0; Mistral.Width = 600.0; Mistral.Height = 800.0; Mistral.Markdown = ""
          Mistral.Blocks =
            [ block "caption" 100.0 40.0 200.0 50.0 "(a) Left panel"
              block "image" 100.0 55.0 280.0 200.0 "![img-0](img-0)"
              block "image" 320.0 55.0 500.0 200.0 "![img-1](img-1)"
              block "caption" 100.0 210.0 500.0 230.0 "Figure 2: Two panels."
              block "text" 100.0 240.0 500.0 400.0 "Body text."
              block "caption" 100.0 420.0 500.0 440.0 "Table 1: Results."
              block "table" 110.0 445.0 490.0 600.0 "| a | b |" ] }
    let found = Ocr.figures [ page ] [| 600.0, 800.0 |]
    Assert.Equal<string list>([ "Fig2"; "Tab1" ], found |> List.map (fun v -> v.Id))
    let fig, tab = found.[0].Parts.[0], found.[1].Parts.[0]
    Assert.Equal(36.0, fig.Y, 3)          // sub-caption above the images, minus the 4 pt margin
    Assert.Equal(234.0, fig.Y + fig.H, 3) // through the caption
    Assert.Equal(504.0, fig.X + fig.W, 3)
    Assert.Equal(416.0, tab.Y, 3)
    Assert.Equal(604.0, tab.Y + tab.H, 3)
    Assert.Equal(VisualKind.Table, found.[1].Kind)
    Assert.Equal(Some "1", found.[1].EqNumber)

[<Fact>]
let ``figures found later are shown where their caption is read and where they are referred to`` () =
    let fig = { eq "Fig1" with Kind = VisualKind.Figure; EqNumber = Some "1" }
    let say i text = { seg i None ShowReason.Own with Say = text }
    let s =
        scriptWith
            [| say 0 "Figure 1: The model architecture."
               say 1 "It has two stacks."
               { say 2 "As in Equation 1." with Show = Some "E1" }
               say 3 "Figure 1 shows the encoder on the left."
               say 4 "Nothing to see." |]
            [| eq "E1" |]
    let s = Narration.attachFigures [ fig ] s
    let shown = s.Segments |> Array.map (fun g -> g.Show, g.Reason)
    Assert.Equal((Some "Fig1", ShowReason.Own), shown.[0])
    Assert.Equal((Some "Fig1", ShowReason.Recent), shown.[1])
    Assert.Equal((Some "E1", ShowReason.Own), shown.[2]) // an equation already shown stays
    Assert.Equal((Some "Fig1", ShowReason.Reference), shown.[3])
    Assert.Contains(s.Visuals, fun v -> v.Id = "Fig1")

[<Fact>]
let ``"Figure 1" and "Equation 1" point at different images`` () =
    let unit id text vis = { Id = id; Kind = UnitKind.Sentence; Text = text; Spoken = text; Visual = vis; Page = 0; Section = 1; ParagraphEnd = false }
    let e1 = { eq "E1" with EqNumber = Some "1" }
    let f1 = { eq "Fig1" with Kind = VisualKind.Figure; EqNumber = Some "1" }
    let a =
        { Title = "T"; PageCount = 1; Sections = [| "T"; "S" |]; Visuals = [| e1; f1 |]
          Units = [| unit "S1" "See Figure 1 for the model." None; unit "S2" "Heading break." None
                     { unit "H1" "2 Method" None with Kind = UnitKind.Heading }; unit "S3" "By Equation 1 we get it." None |] }
    let s = Narration.buildLocal a
    Assert.Equal(Some "Fig1", s.Segments.[0].Show)
    Assert.Equal(Some "E1", s.Segments.[3].Show)

// ---- Ask

let private helpScript () =
    let seg i say show = { Index = i; Kind = UnitKind.Sentence; Say = say; Show = show; Reason = ShowReason.Own; Section = 1; Page = 0; PauseAfterMs = 0 }
    { Version = 1; Title = "T"; PageCount = 1; Narrator = "test"
      Sections = [| { Title = "T"; FirstSegment = 0 }; { Title = "1 Method"; FirstSegment = 0 } |]
      Segments = [| seg 0 "We train with SGD on ImageNet." None; seg 1 "The block computes F(x) + x." (Some "E1") |]
      Visuals = [| { eq "E1" with EqNumber = Some "1"; Latex = Some "y = F(x) + x" } |] }

[<Fact>]
let ``a reply is split into answer, image to show and follow-ups`` () =
    let s = helpScript ()
    let r = Help.parseReply s "The shortcut adds x back.\n\nSo F only learns the change.\n---\nSHOW: [E1]\nNEXT: Why add x? | What is F? | Show an example"
    Assert.Equal("The shortcut adds x back.\n\nSo F only learns the change.", r.Answer)
    Assert.Equal(Some "E1", r.Show)
    Assert.Equal<string list>([ "Why add x?"; "What is F?"; "Show an example" ], r.Followups)
    // an id that isn't in the paper, or none, shows nothing
    Assert.Equal(None, (Help.parseReply s "Answer.\n---\nSHOW: none\nNEXT: a").Show)
    Assert.Equal(None, (Help.parseReply s "Answer.\n---\nSHOW: E9\nNEXT: a").Show)

[<Fact>]
let ``while streaming, the separator and what follows it are held back`` () =
    Assert.Equal("Part of the answer", Help.visibleAnswer "Part of the answer\n-")
    Assert.Equal("Part of the answer", Help.visibleAnswer "Part of the answer\n--")
    Assert.Equal("Part of the answer", Help.visibleAnswer "Part of the answer\n---\nSHOW: E")

[<Fact>]
let ``jargon just heard is offered as what-is questions`` () =
    Assert.Equal<string list>([ "ReLU"; "CIFAR-10"; "ImageNet" ], Help.terms [ "Trained on CIFAR-10 and ImageNet."; "Then a ReLU follows, see Section II." ])
    let s = helpScript ()
    let asks = Help.suggestions s 1 (s.Visual "E1")
    Assert.Equal(Help.Ask.Walkthrough, asks.Head)
    Assert.Contains(Help.Ask.Define "SGD", asks)

[<Fact>]
let ``formulas are split out of answers and left to the screen when spoken`` () =
    let answer = "The output is **the sum**:\n$$y = F(x) + x$$\nwhere $x$ is the input."
    match Help.pieces answer with
    | [ Help.Piece.Prose a; Help.Piece.Formula f; Help.Piece.Prose b ] ->
        Assert.Equal("The output is **the sum**:", a)
        Assert.Equal("y = F(x) + x", f)
        Assert.Equal("where $x$ is the input.", b)
    | other -> failwithf "unexpected %A" other
    Assert.Equal("The output is the sum: y equals F(x) plus x. where x is the input.", Help.spoken answer)

[<Fact>]
let ``LaTeX is read the way a lecturer says it`` () =
    Assert.Equal("W sub i to the power Q", Help.speakLatex "W_i^Q")
    Assert.Equal("the square root of d sub k", Help.speakLatex @"\sqrt{d_k}")
    Assert.Equal("Q K transpose over the square root of d sub k", Help.speakLatex @"\frac{QK^T}{\sqrt{d_k}}")
    Assert.Equal("x squared minus 1", Help.speakLatex "x^2 - 1")

[<Fact>]
let ``the prompt carries the paper, the visuals and where the listener is`` () =
    let s = helpScript ()
    let k = ({ PaperText = "FULL PAPER TEXT"; FigureNotes = Map.empty }: Help.Knowledge)
    let msgs = Help.messages Settings.defaults s k [] 1 (s.Visual "E1") "Why?"
    let system = snd msgs.Head
    let user = snd (List.last msgs)
    Assert.Contains("FULL PAPER TEXT", system)
    Assert.Contains("[E1] Equation (1), page 1. LaTeX: y = F(x) + x", system)
    Assert.Contains("section \"1 Method\"", user)
    Assert.Contains(">>> The block computes F(x) + x.", user)
    Assert.Contains("We train with SGD on ImageNet.", user)
    Assert.Contains("The question is about [E1]", user)
    Assert.EndsWith("QUESTION: Why?", user)

[<Fact>]
let ``an answer is read in clips, the first one short so it starts quickly`` () =
    let answer = "Short start. " + String.replicate 12 "This sentence is part of a longer explanation of the method. "
    let chunks = Help.speechChunks answer
    Assert.True(chunks.Head.Length <= 120)
    Assert.StartsWith("Short start.", chunks.Head)
    Assert.True(chunks.Length >= 3)
    Assert.All(chunks, fun c -> Assert.True(c.Length <= 330))
    Assert.Equal(Help.spoken answer, String.Join(" ", chunks))
