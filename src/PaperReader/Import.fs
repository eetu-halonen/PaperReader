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
let private cropsVersion = 7

let private cropsMarker (paths: Store.Paths) id = Path.Combine(paths.Images id, sprintf "crops-v%d" cropsVersion)

/// Written once Mistral OCR has checked a paper's equations and found its figures and tables.
let private ocrMarker (paths: Store.Paths) id = Path.Combine(paths.Paper id, "ocr-v1")

/// True when opening the paper has work to do first: images from an older renderer, or (with a key)
/// the OCR check and figures, which papers imported before didn't get.
let needsRefresh (settings: Settings) (paths: Store.Paths) (id: string) =
    File.Exists(paths.Pdf id)
    && (not (File.Exists(cropsMarker paths id)) || (Settings.hasKey settings && not (File.Exists(ocrMarker paths id))))

/// Reads the paper with Mistral OCR (one call for the whole PDF): grows equation crops that would cut part
/// of an equation off, records their LaTeX, and finds the figures and tables with their captions.
let private runOcr (settings: Settings) (pdf: string) (visuals: Visual[]) (ct: CancellationToken)
    : Task<Result<Visual[] * Visual list, string>> =
    task {
        try
            let! pages = Mistral.ocr settings.MistralApiKey "application/pdf" (File.ReadAllBytes pdf) ct
            let sizes = Layout.pageSizes pdf
            // the text is kept for answering questions about the paper (Ask)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName pdf, "paper.md"), Help.paperText pages)
            return Ok(Ocr.refine pages sizes visuals, Ocr.figures pages sizes)
        with
        | :? OperationCanceledException -> return raise (OperationCanceledException())
        | Mistral.MistralError(_, m) -> return Error m
        | e -> return Error e.Message
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

/// Brings a paper imported earlier up to date: redraws its images if an older renderer made them, and with
/// a key runs the OCR check and adds figures and tables. Returns the script, updated when that changed it.
let refreshCrops (platform: IPlatform) (settings: Settings) (paths: Store.Paths) (id: string) (script: Script) (progress: Progress) : Task<Script> =
    task {
        let redraw = not (File.Exists(cropsMarker paths id))
        let wantOcr = Settings.hasKey settings && not (File.Exists(ocrMarker paths id))
        if not (File.Exists(paths.Pdf id)) || (not redraw && not wantOcr) then return script
        else
            let! script =
                task {
                    if not wantOcr then return script
                    else
                        progress "Checking the equations and figures with Mistral OCR" None
                        match! runOcr settings (paths.Pdf id) script.Visuals CancellationToken.None with
                        | Ok (visuals, figures) ->
                            File.WriteAllText(ocrMarker paths id, "")
                            return Narration.attachFigures figures { script with Visuals = visuals }
                        | Error _ -> return script // tried again next time
                }
            if redraw && Directory.Exists(paths.Images id) then
                for f in Directory.GetFiles(paths.Images id, "*.png") do
                    File.Delete f
            let total = max 1 script.Visuals.Length
            let! _ =
                renderCrops platform settings paths id script.Visuals (fun k ->
                    progress (sprintf "Drawing the equations and figures (%d of %d)" k total) (Some(float k / float total))) CancellationToken.None
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

            let! analysis, ocrWarning =
                task {
                    if not (Settings.hasKey settings) then return analysis, None
                    else
                        progress "Checking the equations and figures with Mistral OCR" (Some 0.3)
                        match! runOcr settings pdf analysis.Visuals ct with
                        | Ok (visuals, figures) ->
                            File.WriteAllText(ocrMarker paths id, "")
                            return
                                { analysis with Visuals = Array.append visuals (Array.ofList figures); Units = Ocr.linkCaptions figures analysis.Units },
                                None
                        | Error m -> return analysis, Some("the equation and figure check with Mistral OCR failed: " + m)
                }
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
