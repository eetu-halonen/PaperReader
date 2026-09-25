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

            Directory.CreateDirectory(paths.Images id) |> ignore
            let crops = analysis.Visuals |> Array.map (fun v -> v, paths.Image(id, v.Id)) |> List.ofArray
            let total = max 1 crops.Length
            progress "Cutting out the equations" (Some 0.33)
            do! platform.RenderCrops(pdf, crops, (fun k ->
                    progress (sprintf "Cutting out the math (%d of %d)" k total) (Some(0.33 + 0.12 * float k / float total))), ct)

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
