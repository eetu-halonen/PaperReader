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
    let visual = { Id = "E3"; Kind = VisualKind.Algorithm; Parts = [| { Page = 1; X = 1.5; Y = 2.0; W = 30.0; H = 4.0 }; { Page = 1; X = 5.0; Y = 9.0; W = 10.0; H = 3.0 } |]; EqNumber = Some "1"; RawText = "x_{i}" }
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
    let v = { Id = "E1"; Kind = VisualKind.Equation; Parts = [| { Page = 0; X = 0.0; Y = 0.0; W = 1.0; H = 1.0 } |]; EqNumber = Some "4"; RawText = "" }
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
