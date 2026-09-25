/// Development tool: runs the paper analysis and narration on a desktop and prints the result.
/// Usage: ScriptDump <paper.pdf> [--crops <dir>] [--mistral <model>]   (API key from MISTRAL_API_KEY)
open System
open System.IO
open System.Diagnostics
open PaperReader.Core

let renderCrop (pdf: string) (dir: string) (v: Visual) =
    // pdftoppm renders at 3x (216 dpi); crop box coordinates are in points
    let s = 3.0
    let r = v.Rect
    let args =
        sprintf "-png -r 216 -f %d -l %d -x %d -y %d -W %d -H %d -singlefile \"%s\" \"%s\""
            (r.Page + 1) (r.Page + 1) (int (r.X * s)) (int (r.Y * s)) (int (r.W * s)) (int (r.H * s)) pdf (Path.Combine(dir, v.Id))
    use p = Process.Start(ProcessStartInfo("pdftoppm", args, UseShellExecute = false))
    p.WaitForExit()

[<EntryPoint>]
let main argv =
    let pdf = argv.[0]
    let opt name = argv |> Array.tryFindIndex ((=) name) |> Option.map (fun i -> argv.[i + 1])
    if argv |> Array.contains "--lines" then Layout.trace <- Some(fun s -> printfn "%s" s)
    let sw = Stopwatch.StartNew()
    let a = Layout.analyze pdf (fun _ _ -> ())
    eprintfn "analysed %d pages in %dms: %d units, %d visuals" a.PageCount sw.ElapsedMilliseconds a.Units.Length a.Visuals.Length
    printfn "TITLE: %s" a.Title
    printfn "SECTIONS: %s" (String.Join(" | ", a.Sections))
    for u in a.Units do
        let v = u.Visual |> Option.map (fun v -> " {" + v + "}") |> Option.defaultValue ""
        printfn "[%s p%d s%d%s] %s\n      >> %s" u.Id (u.Page + 1) u.Section v (u.Text.Replace("\n", " / ")) u.Spoken
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
