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
let private cropsVersion = 4

let private cropsMarker (paths: Store.Paths) id = Path.Combine(paths.Images id, sprintf "crops-v%d" cropsVersion)

let private renderCrops (platform: IPlatform) (paths: Store.Paths) id (visuals: Visual[]) (progress: int -> unit) (ct: CancellationToken) =
    task {
        Directory.CreateDirectory(paths.Images id) |> ignore
        let crops = visuals |> Array.map (fun v -> v, paths.Image(id, v.Id)) |> List.ofArray
        do! platform.RenderCrops(paths.Pdf id, crops, progress, ct)
        File.WriteAllText(cropsMarker paths id, "")
    }

/// Redraws a paper's images if they were made by an older crop renderer.
let refreshCrops (platform: IPlatform) (paths: Store.Paths) (id: string) (script: Script) : Task =
    task {
        if not (File.Exists(cropsMarker paths id)) && File.Exists(paths.Pdf id) then
            if Directory.Exists(paths.Images id) then
                for f in Directory.GetFiles(paths.Images id) do
                    File.Delete f
            do! renderCrops platform paths id script.Visuals ignore CancellationToken.None
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

            let total = max 1 analysis.Visuals.Length
            progress "Cutting out the equations" (Some 0.33)
            do! renderCrops platform paths id analysis.Visuals (fun k ->
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
