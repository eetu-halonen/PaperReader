/// Development tool: runs the paper analysis and narration on a desktop and prints the result.
/// Usage: ScriptDump <paper.pdf | document | https://address> [--crops <dir>] [--ocr] [--mistral <model>]
/// (API key from MISTRAL_API_KEY). Documents that aren't PDFs go through Formats and Blocks, as the app's import
/// does (their pictures aren't drawn here: --mistral narrates them without images).
open System
open System.IO
open System.Diagnostics
open PaperReader.Core

let renderCrop (pdf: string) (dir: string) (v: Visual) =
    // pdftoppm renders at 3x (216 dpi); crop box coordinates are in points. Parts are stacked with ImageMagick.
    let s = 3.0
    let run (exe: string) (args: string) =
        use p = Process.Start(ProcessStartInfo(exe, args, UseShellExecute = false, RedirectStandardError = true))
        p.StandardError.ReadToEnd() |> ignore
        p.WaitForExit()
    let parts =
        v.Parts |> Array.mapi (fun k r ->
            let stem = Path.Combine(dir, sprintf "%s_part%d" v.Id k)
            run "pdftoppm" (sprintf "-png -r 216 -f %d -l %d -x %d -y %d -W %d -H %d -singlefile \"%s\" \"%s\""
                                (r.Page + 1) (r.Page + 1) (int (r.X * s)) (int (r.Y * s)) (int (r.W * s)) (int (r.H * s)) pdf stem)
            stem + ".png")
    run "convert" (sprintf "%s -bordercolor white -border 0x12 -append \"%s\"" (parts |> Array.map (sprintf "\"%s\"") |> String.concat " ") (Path.Combine(dir, v.Id + ".png")))
    for f in parts do File.Delete f

/// --ask <paper id> <segment> <question>: asks about a paper in the desktop app's cache, as the Ask panel does.
let private askMode (argv: string[]) =
    let paths = Store.Paths(Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "PaperReader"))
    let settings = Store.loadSettings paths
    let id, position, question = argv.[1], int argv.[2], argv.[3]
    let script = (Store.loadScript paths id).Value
    let k = (Help.prepare settings paths id script (eprintfn "%s") Threading.CancellationToken.None).Result
    let about = script.Segments.[position].Show |> Option.bind script.Visual
    let ask =
        match question with
        | "simpler" -> Help.Ask.Simpler
        | "example" -> Help.Ask.Example
        | "why" -> Help.Ask.WhyItMatters
        | "recap" -> Help.Ask.Recap
        | "walk" -> Help.Ask.Walkthrough
        | q -> Help.Ask.Free q
    printfn "AT [%d] %s" position script.Segments.[position].Say
    printfn "SUGGESTED: %s" (Help.suggestions script position about |> List.map (fun a -> Help.questionText a about) |> String.concat " / ")
    let sw = Stopwatch.StartNew()
    let mutable first = 0L
    let turn =
        (Help.ask settings script k [] position about ask (fun t -> if first = 0L && t <> "" then first <- sw.ElapsedMilliseconds) Threading.CancellationToken.None).Result
    printfn "Q: %s\n\n%s\n\nSHOW: %A\nNEXT: %A\n(first text %d ms, done %d ms; prompt %d chars)" turn.Question turn.Answer turn.Show turn.Followups first sw.ElapsedMilliseconds
        (Help.messages settings script k [] position about turn.Question |> List.sumBy (fun (_, t) -> t.Length))
    0

/// --cards <paper id> <segment> <moment|section|visual|paper|topic text>: makes flashcards as the Remember panel
/// does, and prints them (nothing is saved).
let private cardsMode (argv: string[]) =
    let paths = Store.Paths(Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "PaperReader"))
    let settings = Store.loadSettings paths
    let id, position, what = argv.[1], int argv.[2], argv.[3]
    let script = (Store.loadScript paths id).Value
    let k = (Help.prepare settings paths id script (eprintfn "%s") Threading.CancellationToken.None).Result
    let request =
        match what with
        | "moment" -> Cards.Request.Moment
        | "section" -> Cards.Request.Section
        | "visual" -> Cards.Request.Visual(script.Segments.[position].Show |> Option.defaultValue "")
        | "paper" -> Cards.Request.Paper(Cards.deckSize script)
        | t -> Cards.Request.Topic t
    printfn "AT [%d] %s
REQUEST: %s" position script.Segments.[position].Say (Cards.describe script request)
    let sw = Stopwatch.StartNew()
    let cards = (Cards.make settings script k position (Store.loadCards paths id) request ignore Threading.CancellationToken.None).Result
    for c in cards do
        printfn "\nQ: %s\nA: %s\n   (show %A%s, segment %d)" c.Front c.Back c.Visual (if c.VisualOnFront then " on front" else "") c.Segment
    printfn "\n%d cards in %d ms" cards.Length sw.ElapsedMilliseconds
    0

[<EntryPoint>]
let main argv =
    if argv.[0] = "--ask" then askMode argv
    elif argv.[0] = "--cards" then cardsMode argv else
    let pdf = argv.[0]
    let opt name = argv |> Array.tryFindIndex ((=) name) |> Option.map (fun i -> argv.[i + 1])
    if argv |> Array.contains "--lines" then Layout.trace <- Some(fun s -> printfn "%s" s)
    let sw = Stopwatch.StartNew()
    let bytes, name =
        if pdf.StartsWith "http" then (Formats.fetchDocument pdf Threading.CancellationToken.None).Result
        else File.ReadAllBytes pdf, Path.GetFileName pdf
    let format = Formats.detect name bytes
    eprintfn "format: %A" format
    let a =
        match format with
        | Some Formats.Format.Pdf | None -> Layout.analyze pdf (fun _ _ -> ())
        | Some f ->
            let doc = Formats.read f bytes (Path.GetFileNameWithoutExtension name)
            let doc = (Blocks.resolveImages (Formats.fetchImage Threading.CancellationToken.None) doc).Result
            if argv |> Array.contains "--blocks" then
                for b in doc.Blocks do printfn "BLOCK %s" (match b with Blocks.Block.Image (Blocks.Data d, alt, c) -> sprintf "Image %d bytes %A alt=%s caption=%s" d.Length (Blocks.imageSize d) alt c | b -> sprintf "%A" b)
            fst (Blocks.analyze doc)
    // --ocr: Mistral OCR checks the equations and finds figures and tables, as the app's import does
    let a =
        if not (argv |> Array.contains "--ocr") then a
        else
            let pages = (Mistral.ocr (Environment.GetEnvironmentVariable "MISTRAL_API_KEY") "application/pdf" (File.ReadAllBytes pdf) Threading.CancellationToken.None).Result
            let sizes = Layout.pageSizes pdf
            let figures = Ocr.figures pages sizes
            { a with Visuals = Array.append (Ocr.refine pages sizes a.Visuals) (Array.ofList figures); Units = Ocr.linkCaptions figures a.Units }
    eprintfn "analysed %d pages in %dms: %d units, %d visuals" a.PageCount sw.ElapsedMilliseconds a.Units.Length a.Visuals.Length
    printfn "TITLE: %s" a.Title
    printfn "SECTIONS: %s" (String.Join(" | ", a.Sections))
    for u in a.Units do
        let v = u.Visual |> Option.map (fun v -> " {" + v + "}") |> Option.defaultValue ""
        printfn "[%s p%d s%d%s] %s\n      >> %s" u.Id (u.Page + 1) u.Section v (u.Text.Replace("\n", " / ")) u.Spoken
    for v in a.Visuals do
        printfn "VISUAL %s %A %s" v.Id v.Kind (v.Parts |> Array.map (fun r -> sprintf "p%d x=%.1f y=%.1f w=%.1f h=%.1f" (r.Page + 1) r.X r.Y r.W r.H) |> String.concat " | ")
    match opt "--crops" with
    | Some dir ->
        Directory.CreateDirectory dir |> ignore
        for v in a.Visuals do renderCrop pdf dir v
        eprintfn "rendered %d crops to %s" a.Visuals.Length dir
    | None -> ()
    match opt "--mistral" with
    | Some model ->
        Narration.trace <- Some(fun s -> eprintfn "TRACE %s" s)
        let key = Environment.GetEnvironmentVariable "MISTRAL_API_KEY"
        let dir = defaultArg (opt "--crops") (Path.GetTempPath())
        let image (v: Visual) =
            let f = Path.Combine(dir, v.Id + ".png")
            if File.Exists f then Some(File.ReadAllBytes f) else None
        let sw = Stopwatch.StartNew()
        let script, warning =
            (Narration.buildWithMistral key model a image (fun p -> eprintfn "narration %d/%d (offline %d)" p.Done p.Total p.FellBack) Threading.CancellationToken.None).Result
        eprintfn "narrated in %.1fs; warning=%A; narrator=%s" sw.Elapsed.TotalSeconds warning script.Narrator
        printfn "\n===== MISTRAL SCRIPT ====="
        for s in script.Segments do
            printfn "%4d %-9A show=%-6s %-9A | %s" s.Index s.Kind (defaultArg s.Show "-") s.Reason s.Say
    | None ->
        let script = Narration.buildLocal a
        printfn "\n===== OFFLINE SCRIPT: %d segments, %d sections =====" script.Segments.Length script.Sections.Length
        for s in script.Segments |> Array.truncate 40 do
            printfn "%4d show=%-6s %-9A | %s" s.Index (defaultArg s.Show "-") s.Reason s.Say
    0
