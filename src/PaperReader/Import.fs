/// Turning a PDF into a cached, narrated paper.
module PaperReader.Import

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open PaperReader.Core

type Progress = string -> float option -> unit

let private sentenceCount (a: Analysis) =
    a.Units |> Array.filter (fun u -> u.Kind = UnitKind.Sentence) |> Array.length

/// Bumped when the crop renderer changes, so papers imported earlier get their images redrawn.
let private cropsVersion = 6

let private cropsMarker (paths: Store.Paths) id = Path.Combine(paths.Images id, sprintf "crops-v%d" cropsVersion)

/// True when a paper's images were made by an older renderer and will be redrawn when it is opened.
let needsRefresh (paths: Store.Paths) (id: string) =
    File.Exists(paths.Pdf id) && not (File.Exists(cropsMarker paths id))

/// Reads every display equation with Mistral OCR (one call for the whole PDF), grows crops that would
/// cut part of an equation off, and records the LaTeX. Without a key, or if OCR fails, returns the visuals as they are.
let private checkWithOcr (settings: Settings) (pdf: string) (visuals: Visual[]) (ct: CancellationToken) : Task<Visual[] * string option> =
    task {
        if not (Settings.hasKey settings) || not (visuals |> Array.exists (fun v -> v.Kind = VisualKind.Equation)) then
            return visuals, None
        else
            try
                let! pages = Mistral.ocr settings.MistralApiKey "application/pdf" (File.ReadAllBytes pdf) ct
                return Ocr.refine pages (Layout.pageSizes pdf) visuals, None
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException())
            | Mistral.MistralError(_, m) -> return visuals, Some("the equation check with Mistral OCR failed: " + m)
            | e -> return visuals, Some("the equation check with Mistral OCR failed: " + e.Message)
    }

/// OCR of one cut-off crop, for typesetting it instead. Inline crops hold only formula pieces, so all
/// the math found belongs to the visual; for a display equation, the pieces that match its text.
let private ocrCrop (settings: Settings) (ct: CancellationToken) : (Visual -> byte[] -> Task<string option>) option =
    if not (Settings.hasKey settings) then None
    else
        Some(fun (v: Visual) (png: byte[]) ->
            task {
                try
                    let! pages = Mistral.ocr settings.MistralApiKey "image/png" png ct
                    let pieces = pages |> List.collect (fun p -> Ocr.mathPieces p.Markdown)
                    let pieces = if v.Kind = VisualKind.Inline then pieces else Ocr.bestPieces v.RawText pieces
                    return (if pieces.IsEmpty then None else Some(String.Join("\n", pieces)))
                with _ -> return None
            })

let private renderCrops (platform: IPlatform) (settings: Settings) (paths: Store.Paths) id (visuals: Visual[]) (progress: int -> unit) (ct: CancellationToken) =
    task {
        Directory.CreateDirectory(paths.Images id) |> ignore
        let crops = visuals |> Array.map (fun v -> v, paths.Image(id, v.Id)) |> List.ofArray
        let sizes = Layout.pageSizes (paths.Pdf id)
        let! outcomes =
            Task.Run<(string * Crops.Outcome) list>(fun () ->
                task {
                    use pdf = platform.OpenPdf(paths.Pdf id)
                    return! Crops.render pdf sizes crops (ocrCrop settings ct) progress ct
                })
        File.WriteAllText(cropsMarker paths id, "")
        return outcomes
    }

/// Redraws a paper's images if they were made by an older renderer, checking its equations with OCR first
/// when a key is set. Returns the script, updated when OCR changed it.
let refreshCrops (platform: IPlatform) (settings: Settings) (paths: Store.Paths) (id: string) (script: Script) (progress: Progress) : Task<Script> =
    task {
        if not (needsRefresh paths id) then return script
        else
            let needsOcr = script.Visuals |> Array.exists (fun v -> v.Kind = VisualKind.Equation && v.Latex.IsNone)
            if needsOcr && Settings.hasKey settings then progress "Checking the equations with Mistral OCR" None
            let! visuals, _ = if needsOcr then checkWithOcr settings (paths.Pdf id) script.Visuals CancellationToken.None else Task.FromResult(script.Visuals, None)
            let script = if obj.ReferenceEquals(visuals, script.Visuals) then script else { script with Visuals = visuals }
            if Directory.Exists(paths.Images id) then
                for f in Directory.GetFiles(paths.Images id, "*.png") do
                    File.Delete f
            let total = max 1 visuals.Length
            let! _ =
                renderCrops platform settings paths id visuals (fun k ->
                    progress (sprintf "Redrawing the equations (%d of %d)" k total) (Some(float k / float total))) CancellationToken.None
            Store.saveScript paths id script
            return script
    }

/// Analyses, crops the math, narrates and caches a PDF. A paper imported before is returned from the cache.
let run (platform: IPlatform) (settings: Settings) (source: string) (displayName: string) (progress: Progress) (ct: CancellationToken)
    : Task<PaperInfo * Script * string option> =
    task {
        let paths = Store.Paths platform.DataDir
        let id = Store.paperId source
        Directory.CreateDirectory(paths.Paper id) |> ignore
        let pdf = paths.Pdf id
        if not (File.Exists pdf) then File.Copy(source, pdf)
        match Store.loadScript paths id, Store.loadMeta paths id with
        | Some script, Some meta -> return meta, script, None
        | _ ->
            progress "Reading the paper" (Some 0.02)
            let! analysis =
                Task.Run(fun () ->
                    Layout.analyze pdf (fun page pages ->
                        progress (sprintf "Reading page %d of %d" page pages) (Some(0.02 + 0.3 * float page / float pages))))
            if sentenceCount analysis < 3 then
                failwith "No readable text was found in this PDF. Scanned papers (pictures of pages) aren't supported."
            ct.ThrowIfCancellationRequested()

            if Settings.hasKey settings then progress "Checking the equations with Mistral OCR" (Some 0.3)
            let! visuals, ocrWarning = checkWithOcr settings pdf analysis.Visuals ct
            let analysis = { analysis with Visuals = visuals }
            ct.ThrowIfCancellationRequested()

            let total = max 1 analysis.Visuals.Length
            progress "Cutting out the equations" (Some 0.33)
            let! _ =
                renderCrops platform settings paths id analysis.Visuals (fun k ->
                    progress (sprintf "Cutting out the math (%d of %d)" k total) (Some(0.33 + 0.12 * float k / float total))) ct

            let title =
                if String.IsNullOrWhiteSpace analysis.Title || analysis.Title = "paper" then Path.GetFileNameWithoutExtension displayName
                else analysis.Title
            let analysis = { analysis with Title = title }
            let! script, warning =
                if Settings.hasKey settings && settings.UseMistralNarration then
                    let image (v: Visual) =
                        let f = paths.Image(id, v.Id)
                        if File.Exists f then Some(File.ReadAllBytes f) else None
                    Narration.buildWithMistral settings.MistralApiKey settings.NarrationModel analysis image
                        (fun p ->
                            progress
                                (sprintf "Writing the narration with Mistral (%d of %d parts)" p.Done p.Total)
                                (Some(0.45 + 0.55 * float p.Done / float (max 1 p.Total))))
                        ct
                else Task.FromResult(Narration.buildLocal analysis, None)
            let warning =
                [ warning |> Option.map (fun w -> "Some parts use the offline narration: " + w); ocrWarning |> Option.map (fun w -> "Note: " + w + ".") ]
                |> List.choose (fun w -> w)
                |> function [] -> None | ws -> Some(String.Join(" ", ws))
            Store.saveScript paths id script
            let meta =
                { Id = id
                  Title = title
                  PageCount = analysis.PageCount
                  AddedUtc = DateTime.UtcNow
                  SegmentCount = script.Segments.Length
                  LastSegment = 0 }
            Store.saveMeta paths meta
            return meta, script, warning
    }
