/// Turning a document (a PDF, or any other format Formats reads) into a cached, narrated paper.
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
                // only crops are redrawn; pictures OCR cut out of a scanned PDF are kept
                for v in script.Visuals do
                    if v.Parts.Length > 0 && File.Exists(paths.Image(id, v.Id)) then File.Delete(paths.Image(id, v.Id))
            let total = max 1 script.Visuals.Length
            let! _ =
                renderCrops platform settings paths id script.Visuals (fun k ->
                    progress (sprintf "Drawing the equations and figures (%d of %d)" k total) (Some(float k / float total))) CancellationToken.None
            Store.saveScript paths id script
            return script
    }

/// Leaves out visuals that couldn't be drawn, and the units' links to them.
let private withoutVisuals (missing: Set<string>) (a: Analysis) =
    if missing.IsEmpty then a
    else
        { a with
            Visuals = a.Visuals |> Array.filter (fun v -> not (missing.Contains v.Id))
            Units = a.Units |> Array.map (fun u -> match u.Visual with Some v when missing.Contains v -> { u with Visual = None } | _ -> u) }

/// A document read into blocks: its pictures fetched and drawn, its text kept for questions. Returns the
/// analysis, ready to narrate.
let private fromBlocks (paths: Store.Paths) (id: string) (doc: Blocks.Document) (progress: Progress) (ct: CancellationToken) : Task<Analysis> =
    task {
        let links = doc.Blocks |> List.filter (function Blocks.Block.Image (Blocks.Link _, _, _) -> true | _ -> false) |> List.length
        if links > 0 then progress (sprintf "Downloading the pictures (%d)" links) (Some 0.1)
        let! doc = Blocks.resolveImages (Formats.fetchImage ct) doc
        ct.ThrowIfCancellationRequested()
        let analysis, pictures = Blocks.analyze doc
        if analysis.Units |> Array.forall (fun u -> u.Kind = UnitKind.Title) then
            failwith "No readable text was found in this document."
        Directory.CreateDirectory(paths.Images id) |> ignore
        let total = max 1 pictures.Length
        let failed = Collections.Generic.HashSet<string>()
        pictures |> List.iteri (fun k (vid, picture) ->
            ct.ThrowIfCancellationRequested()
            progress (sprintf "Drawing the pictures, equations and tables (%d of %d)" (k + 1) total) (Some(0.15 + 0.3 * float (k + 1) / float total))
            let output = paths.Image(id, vid)
            if not (File.Exists output) && not (Crops.drawPicture picture output) then failed.Add vid |> ignore)
        File.WriteAllText(cropsMarker paths id, "")
        let analysis = withoutVisuals (set failed) analysis
        // the text Ask and the card maker read
        File.WriteAllText(paths.PaperText id, Blocks.toMarkdown doc analysis)
        return analysis
    }

/// Reads pages with Mistral OCR, pictures included (a photo of pages, or a scanned PDF without text).
let private ocrPages (settings: Settings) (paths: Store.Paths) (id: string) (mime: string) (bytes: byte[]) (fallbackTitle: string)
                     (progress: Progress) (ct: CancellationToken) =
    task {
        progress "Reading the pages with Mistral OCR" (Some 0.05)
        let! pages = Mistral.ocrDocument settings.MistralApiKey mime bytes true ct
        File.WriteAllText(ocrMarker paths id, "")
        let doc = Formats.fromOcr pages None fallbackTitle "photos or scans of pages, read by OCR (math as $LaTeX$)"
        return { doc with Blocks = doc.Blocks |> Markup.dropReferences |> Blocks.attachCaptions }
    }

/// Reads a PDF with Mistral OCR: the text, the equations as LaTeX (typeset from it, so what is shown is what is
/// read) and the tables; figures are cut out of the PDF where OCR found them.
let private ocrPdf (platform: IPlatform) (settings: Settings) (paths: Store.Paths) (id: string) (fallbackTitle: string)
                   (progress: Progress) (ct: CancellationToken) : Task<Analysis> =
    task {
        let pdf = paths.Pdf id
        progress "Reading the paper with Mistral OCR" (Some 0.05)
        let! pages = Mistral.ocr settings.MistralApiKey "application/pdf" (File.ReadAllBytes pdf) ct
        let sizes = Layout.pageSizes pdf
        let doc = Formats.fromOcr pages (Some sizes) fallbackTitle "a PDF (most likely a research paper) read by Mistral OCR: math is given as exact $LaTeX$"
        let doc = { doc with Blocks = doc.Blocks |> Markup.dropReferences |> Blocks.attachCaptions }
        let! analysis = fromBlocks paths id doc progress ct
        let figures = analysis.Visuals |> Array.filter (fun v -> v.Parts.Length > 0)
        let! outcomes =
            renderCrops platform settings paths id figures (fun k ->
                progress (sprintf "Cutting out the figures (%d of %d)" k figures.Length) (Some(0.45 * float k / float (max 1 figures.Length)))) ct
        File.WriteAllText(ocrMarker paths id, "")
        let missing = outcomes |> List.filter (fun (v, _) -> not (File.Exists(paths.Image(id, v)))) |> List.map fst |> set
        return { withoutVisuals missing analysis with PageCount = max analysis.PageCount sizes.Length }
    }

/// Analyses, draws the visuals, narrates and caches a document: a PDF (layout analysis and math crops; OCR when
/// it is a scan), a photo of pages (OCR), or any other format Formats reads. A document imported before is
/// returned from the cache.
let run (platform: IPlatform) (settings: Settings) (source: string) (displayName: string) (progress: Progress) (ct: CancellationToken)
    : Task<PaperInfo * Script * string option> =
    task {
        let paths = Store.Paths platform.DataDir
        let bytes = File.ReadAllBytes source
        // a link (shared from a browser, or a file holding just an address) stands for the document it points to
        let original = source
        let! source, displayName, bytes =
            task {
                match Formats.addressIn bytes with
                | Some url ->
                    progress (sprintf "Downloading %s" (Uri url).Host) None
                    let! downloaded, name = Formats.fetchDocument url ct
                    let incoming = Path.Combine(platform.DataDir, "incoming")
                    Directory.CreateDirectory incoming |> ignore
                    let path = Path.Combine(incoming, Guid.NewGuid().ToString("N") + Path.GetExtension name)
                    File.WriteAllBytes(path, downloaded)
                    return path, name, downloaded
                | None -> return source, displayName, bytes
            }
        let format =
            match Formats.detect displayName bytes with
            | Some f -> f
            | None -> failwith (Formats.unsupported displayName)
        let id = Store.paperId source
        Directory.CreateDirectory(paths.Paper id) |> ignore
        let stored = match format with Formats.Format.Pdf -> paths.Pdf id | f -> paths.Document(id, Formats.extension f)
        if not (File.Exists stored) then File.Copy(source, stored)
        if source <> original then try File.Delete source with _ -> () // the download's temporary copy
        let fallbackTitle = Path.GetFileNameWithoutExtension displayName
        match Store.loadScript paths id, Store.loadMeta paths id with
        | Some script, Some meta -> return meta, script, None
        | _ ->
            let! analysis, ocrWarning =
                task {
                    match format with
                    | Formats.Format.Pdf when Settings.hasKey settings ->
                        let! read =
                            task {
                                try
                                    let! a = ocrPdf platform settings paths id fallbackTitle progress ct
                                    return Ok a
                                with
                                | :? OperationCanceledException -> return raise (OperationCanceledException())
                                | Mistral.MistralError (_, m) -> return Error m
                                | e -> return Error e.Message
                            }
                        match read with
                        | Ok a -> return a, None
                        | Error m ->
                            // offline, or OCR failed: the layout analysis reads it instead
                            progress "Reading the paper" (Some 0.02)
                            let! analysis = Task.Run(fun () -> Layout.analyze stored (fun _ _ -> ()))
                            ct.ThrowIfCancellationRequested()
                            if sentenceCount analysis < 3 then return failwith ("Mistral OCR could not read this PDF: " + m)
                            else
                                let! _ = renderCrops platform settings paths id analysis.Visuals ignore ct
                                return analysis, Some("reading with Mistral OCR failed, so equations come from the page layout: " + m)
                    | Formats.Format.Pdf ->
                        let pdf = stored
                        progress "Reading the paper" (Some 0.02)
                        let! analysis =
                            Task.Run(fun () ->
                                Layout.analyze pdf (fun page pages ->
                                    progress (sprintf "Reading page %d of %d" page pages) (Some(0.02 + 0.3 * float page / float pages))))
                        ct.ThrowIfCancellationRequested()
                        if sentenceCount analysis < 3 then
                            // a scan: pictures of pages, no text to extract
                            if not (Settings.hasKey settings) then
                                return failwith "No readable text was found in this PDF: it looks like a scan (pictures of pages). Add a Mistral API key in Settings to read scans with OCR."
                            else
                                let! doc = ocrPages settings paths id "application/pdf" bytes fallbackTitle progress ct
                                let! a = fromBlocks paths id doc progress ct
                                return { a with PageCount = max a.PageCount analysis.PageCount }, None
                        else
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
                            return analysis, ocrWarning
                    | Formats.Format.Image mime ->
                        if not (Settings.hasKey settings) then
                            return failwith "Reading a photo of pages needs Mistral OCR: add a Mistral API key in Settings."
                        else
                            let! doc = ocrPages settings paths id mime bytes fallbackTitle progress ct
                            let! a = fromBlocks paths id doc progress ct
                            return a, None
                    | f ->
                        progress (sprintf "Reading the %s" ((Formats.describe (Formats.key f)).ToLowerInvariant())) (Some 0.02)
                        let! doc = Task.Run(fun () -> Formats.read f bytes fallbackTitle)
                        let! a = fromBlocks paths id doc progress ct
                        return a, None
                }
            ct.ThrowIfCancellationRequested()

            let title =
                if String.IsNullOrWhiteSpace analysis.Title || analysis.Title = "paper" then fallbackTitle
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
                  Format = Formats.key format
                  PageCount = analysis.PageCount
                  AddedUtc = DateTime.UtcNow
                  SegmentCount = script.Segments.Length
                  LastSegment = 0 }
            Store.saveMeta paths meta
            return meta, script, warning
    }
