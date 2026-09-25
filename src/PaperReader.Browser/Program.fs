module PaperReader.Browser.Program

open System
open System.IO
open System.Net.Http
open System.Runtime.InteropServices.JavaScript
open Avalonia
open Avalonia.Browser
open PaperReader

/// `?pdf=<address>` opens that PDF (on this site, or any site that allows it) as if picked.
let private openFromAddress (page: Uri) (platform: BrowserPlatform) (dataDir: string) =
    task {
        let query = page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        match query |> Array.tryFind (fun p -> p.StartsWith "pdf=") with
        | Some p ->
            try
                let address = Uri(page, Uri.UnescapeDataString(p.Substring 4))
                use http = new HttpClient()
                let! bytes = http.GetByteArrayAsync address
                let incoming = Path.Combine(dataDir, "incoming")
                Directory.CreateDirectory incoming |> ignore
                let path = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".pdf")
                File.WriteAllBytes(path, bytes)
                let name = Path.GetFileName address.LocalPath
                platform.Open(path, (if String.IsNullOrWhiteSpace name then "paper.pdf" else name))
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
