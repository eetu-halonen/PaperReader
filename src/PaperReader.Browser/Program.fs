module PaperReader.Browser.Program

open System
open System.IO
open System.Runtime.InteropServices.JavaScript
open Avalonia
open Avalonia.Browser
open PaperReader

/// `?url=<address>` (or `?pdf=`) opens the document there (on this site, or any site that allows it) as if picked:
/// a PDF, a web page, an EPUB, ...
let private openFromAddress (page: Uri) (platform: BrowserPlatform) (dataDir: string) =
    task {
        let query = page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        match query |> Array.tryFind (fun p -> p.StartsWith "pdf=" || p.StartsWith "url=") with
        | Some p ->
            try
                let address = Uri(page, Uri.UnescapeDataString(p.Substring 4))
                let! bytes, name = PaperReader.Core.Formats.fetchDocument address.AbsoluteUri Threading.CancellationToken.None
                let incoming = Path.Combine(dataDir, "incoming")
                Directory.CreateDirectory incoming |> ignore
                let path = Path.Combine(incoming, Guid.NewGuid().ToString("N") + Path.GetExtension name)
                File.WriteAllBytes(path, bytes)
                platform.Open(path, name)
            with e -> eprintfn "could not open %s: %s" p e.Message
        | None -> ()
    }

/// Started by main.js with the page's address.
[<EntryPoint>]
let main argv =
    task {
        let page = if argv.Length > 0 then Uri argv.[0] else Uri "http://localhost/"
        let! _ = JSHost.ImportAsync(Js.Module, Uri(page, "interop.js").ToString())
        let storage = BrowserStorage("/paperreader")
        try do! storage.Start()
        with e -> eprintfn "restoring from IndexedDB failed: %O" e
        let platform = BrowserPlatform(storage)
        Services.platform <- Some(platform :> IPlatform)
        // changes go back to IndexedDB every few seconds, and at once when the page is hidden or closed
        let sync () =
            task {
                try do! storage.Sync()
                with e -> eprintfn "saving to IndexedDB failed: %O" e
            }
            |> ignore
        Js.Every(3000, Action sync)
        Js.OnHidden(Action sync)
        openFromAddress page platform storage.Root |> ignore
        // runs as long as the page is open
        do! AppBuilder.Configure<App>().WithInterFont().StartBrowserAppAsync("out")
    }
    |> ignore
    0
