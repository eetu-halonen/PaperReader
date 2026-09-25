module PaperReader.Desktop.Program

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Media.Imaging
open Avalonia.Threading
open PaperReader

let mutable private snapshotTimer: Threading.Timer = null

/// Debugging aid: with PAPERREADER_SNAPSHOT=<file.png>, the window is saved to that file every 2 s
/// (for checking the UI where screenshots aren't allowed).
let private snapshots () =
    match Environment.GetEnvironmentVariable "PAPERREADER_SNAPSHOT" with
    | null | "" -> ()
    | path ->
        let save () =
            match Application.Current.ApplicationLifetime with
            | :? IClassicDesktopStyleApplicationLifetime as d when not (isNull d.MainWindow) && d.MainWindow.Bounds.Width > 0.0 ->
                let w = d.MainWindow
                use bmp = new RenderTargetBitmap(PixelSize(int w.Bounds.Width, int w.Bounds.Height), Vector(96.0, 96.0))
                bmp.Render w
                bmp.Save(path + ".tmp")
                IO.File.Move(path + ".tmp", path, true)
            | _ -> ()
        snapshotTimer <- new Threading.Timer((fun _ -> Dispatcher.UIThread.Post save), null, 2000, 2000)

/// paper-reader [paper.pdf]
[<EntryPoint; STAThread>]
let main argv =
    let platform = DesktopPlatform()
    Services.platform <- Some(platform :> IPlatform)
    for arg in argv do
        if IO.File.Exists arg then platform.Open(IO.Path.GetFullPath arg)
    snapshots ()
    let code =
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(argv)
    platform.Shutdown()
    code
