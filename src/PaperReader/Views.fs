module PaperReader.Views

open System
open System.Collections.Generic
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Shapes
open Avalonia.Controls.Documents
open Avalonia.FuncUI
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Media.Imaging
open PaperReader.Core
open PaperReader.State

module Palette =
    let bg = "#0E1014"
    let surface = "#181B22"
    let surfaceHigh = "#232833"
    let line = "#2C3240"
    let text = "#ECEEF3"
    let muted = "#9AA1AF"
    let faint = "#646B79"
    let accent = "#8AB4F8"
    let onAccent = "#0B1220"
    let danger = "#F28B82"
    let paper = "#FFFFFF"
    let ink = "#555B66"
    /// A right answer, and the background of an option that is right or was picked wrong.
    let good = "#81C995"
    let goodBg = "#1A3325"
    let badBg = "#3A2222"
    /// Panels that suggest something to do (a review, a key, studying).
    let note = "#1A2436"

// ---------------------------------------------------------------------------------------------
// Images: equation crops are decoded once and kept while they are likely to be shown again.
// ---------------------------------------------------------------------------------------------

let private bitmaps = Dictionary<string, Bitmap>()
let private recent = LinkedList<string>()

let bitmap (path: string) : Bitmap option =
    match bitmaps.TryGetValue path with
    | true, b -> Some b
    | _ when File.Exists path ->
        try
            let b = new Bitmap(path)
            bitmaps.[path] <- b
            recent.AddLast path |> ignore
            // keep memory bounded; evicted bitmaps are left to the GC in case a control still holds one
            while recent.Count > 64 do
                bitmaps.Remove recent.First.Value |> ignore
                recent.RemoveFirst()
            Some b
        with _ -> None
    | _ -> None

// ---------------------------------------------------------------------------------------------
// Small building blocks
// ---------------------------------------------------------------------------------------------

module Icons =
    let play = "M8 5.2 L19 12 L8 18.8 Z"
    let pause = "M6.5 5 H10.5 V19 H6.5 Z M13.5 5 H17.5 V19 H13.5 Z"
    let back = "M9.43 4.95 A 7.5 7.5 0 1 1 4.61 10.7 M12.6 2.1 L9.43 4.95 L12.6 7.8"
    let forward = "M14.57 4.95 A 7.5 7.5 0 1 0 19.39 10.7 M11.4 2.1 L14.57 4.95 L11.4 7.8"
    let chevronLeft = "M15 5 L8 12 L15 19"
    let list = "M5 7 H19 M5 12 H19 M5 17 H14"
    let close = "M6 6 L18 18 M18 6 L6 18"
    let sliders = "M4 7 H11 M15 7 H20 M13 4.5 V9.5 M4 17 H7 M11 17 H20 M9 14.5 V19.5"
    let trash = "M5 7 H19 M10 7 V4.5 H14 V7 M7 7 L8 19.5 H16 L17 7"
    let expand = "M4 9 V4 H9 M15 4 H20 V9 M20 15 V20 H15 M9 20 H4 V15"
    let plus = "M12 5 V19 M5 12 H19"
    let sigma = "M17 5 H7 L13 12 L7 19 H17"
    let ask = "M5 4.5 H19 A2 2 0 0 1 21 6.5 V15 A2 2 0 0 1 19 17 H10.5 L6.5 20.5 V17 H5 A2 2 0 0 1 3 15 V6.5 A2 2 0 0 1 5 4.5 Z M9.8 9 A2.2 2.2 0 1 1 12.9 11 C12.3 11.3 12 11.8 12 12.5 M12 14.7 V14.8"
    let mic = "M12 3 A3 3 0 0 1 15 6 V11 A3 3 0 0 1 9 11 V6 A3 3 0 0 1 12 3 Z M5.5 11 A6.5 6.5 0 0 0 18.5 11 M12 17.5 V21"
    let send = "M5 12 H19 M13 6 L19 12 L13 18"
    let speaker = "M4 9.5 H7.5 L12 5.5 V18.5 L7.5 14.5 H4 Z M15.5 9 A4 4 0 0 1 15.5 15 M18 6.5 A7.5 7.5 0 0 1 18 17.5"
    let stop = "M7 7 H17 V17 H7 Z"
    let search = "M10.5 4 A6.5 6.5 0 1 1 10.5 17 A6.5 6.5 0 1 1 10.5 4 Z M15.3 15.3 L20 20"
    let refresh = "M19 12 A7 7 0 1 1 16.95 7.05 M17.5 3.5 V7.5 H13.5"
    let walk = "M13.5 3.2 A1.7 1.7 0 1 1 13.5 6.6 A1.7 1.7 0 1 1 13.5 3.2 Z M12.8 9 L11.2 14.8 L8.2 20.5 M11.2 14.8 L14.3 16.8 L15.4 20.5 M8.3 12.6 L10.2 9.6 L12.8 9 L14.9 11.8 L17.4 12.8"
    let cards = "M8 4.5 H19 A1.5 1.5 0 0 1 20.5 6 V15.5 M4.5 8 H15 A1.5 1.5 0 0 1 16.5 9.5 V18.5 A1.5 1.5 0 0 1 15 20 H4.5 A1.5 1.5 0 0 1 3 18.5 V9.5 A1.5 1.5 0 0 1 4.5 8 Z"
    let study = "M2.5 9 L12 4.5 L21.5 9 L12 13.5 Z M6.5 11 V15.8 C6.5 17.3 9 18.8 12 18.8 C15 18.8 17.5 17.3 17.5 15.8 V11 M21.5 9 V14.5"
    let check = "M5 12.5 L10 17.5 L19 7"

let icon (data: string) (color: string) (size: float) (filled: bool) : IView =
    Viewbox.create [
        Viewbox.width size
        Viewbox.height size
        Viewbox.isHitTestVisible false
        Viewbox.child (
            Canvas.create [
                Canvas.width 24.0
                Canvas.height 24.0
                Canvas.children [
                    Path.create [
                        Path.data data
                        if filled then Path.fill color
                        else
                            Path.stroke color
                            Path.strokeThickness 2.0
                            Path.strokeLineCap PenLineCap.Round
                            Path.strokeJoin PenLineJoin.Round
                    ]
                ]
            ]
        )
    ]

/// A borderless button. Never pass an attribute twice in FuncUI: creation applies the last one but
/// updates diff against the first, so callers set padding and alignment themselves.
let private plainButton (background: string) (attrs: IAttr<Button> list) =
    Button.create (Button.background background :: Button.borderThickness 0.0 :: attrs)

let iconButton (data: string) (size: float) (onClick: unit -> unit) (key: obj) : IView =
    plainButton "Transparent" [
        Button.width (size + 24.0)
        Button.height (size + 24.0)
        Button.padding 0.0
        Button.horizontalContentAlignment HorizontalAlignment.Center
        Button.verticalContentAlignment VerticalAlignment.Center
        Button.cornerRadius ((size + 24.0) / 2.0)
        Button.content (icon data Palette.text size false)
        Button.onClick ((fun _ -> onClick ()), SubPatchOptions.OnChangeOf key)
    ]

let label (text: string) (size: float) (color: string) : IView =
    TextBlock.create [
        TextBlock.text text
        TextBlock.fontSize size
        TextBlock.foreground color
        TextBlock.textWrapping TextWrapping.Wrap
    ]

let private pill (text: string) (onClick: unit -> unit) (primary: bool) : IView =
    Button.create [
        Button.content text
        Button.fontSize 15.0
        Button.fontWeight FontWeight.SemiBold
        Button.padding (Thickness(18.0, 10.0))
        Button.cornerRadius 22.0
        Button.background (if primary then Palette.accent else Palette.surfaceHigh)
        Button.foreground (if primary then Palette.onAccent else Palette.text)
        Button.onClick ((fun _ -> onClick ()), SubPatchOptions.OnChangeOf text)
    ]

let private chip (text: string) (onClick: unit -> unit) : IView =
    Button.create [
        Button.content text
        Button.fontSize 14.0
        Button.padding (Thickness(14.0, 8.0))
        Button.margin (Thickness(0.0, 0.0, 8.0, 8.0))
        Button.cornerRadius 18.0
        Button.background Palette.surfaceHigh
        Button.foreground Palette.text
        Button.borderBrush Palette.line
        Button.borderThickness 1.0
        Button.onClick ((fun _ -> onClick ()), SubPatchOptions.OnChangeOf text)
    ]

let private textLink (text: string) (onClick: unit -> unit) (key: obj) : IView =
    plainButton "Transparent" [
        Button.padding (Thickness(0.0, 4.0, 16.0, 4.0))
        Button.foreground Palette.accent
        Button.fontSize 14.0
        Button.content text
        Button.onClick ((fun _ -> onClick ()), SubPatchOptions.OnChangeOf key)
    ]

let private capitalize (s: string) = if s = "" then s else string (Char.ToUpperInvariant s.[0]) + s.Substring 1

/// "12 pages" for a PDF, "EPUB book · 9 chapters" for other documents.
let private sizeLabel (p: PaperInfo) =
    let noun = Formats.pageNoun p.Format
    let count = sprintf "%d %s%s" p.PageCount noun (if p.PageCount = 1 then "" else "s")
    if p.Format = "pdf" then count else sprintf "%s · %s" (Formats.describe p.Format) count

let private formatMinutes (ms: int) =
    let minutes = int (Math.Round(float ms / 60000.0))
    if ms <= 0 then "done"
    elif minutes < 1 then "under a minute left"
    elif minutes < 60 then sprintf "%d min left" minutes
    else sprintf "%d h %d min left" (minutes / 60) (minutes % 60)

// ---------------------------------------------------------------------------------------------
// Library
// ---------------------------------------------------------------------------------------------

let private paperCard (model: Model) (p: PaperInfo) (dispatch: Msg -> unit) : IView =
    let progress =
        if p.SegmentCount > 0 && p.LastSegment > 0 then sprintf " · %d%% listened" (100 * p.LastSegment / p.SegmentCount) else ""
    let progress =
        match Cards.dueCount DateTime.UtcNow (deckOf model p.Id) with
        | 0 -> progress
        | 1 -> progress + " · 1 card due"
        | n -> progress + sprintf " · %d cards due" n
    let confirming = model.ConfirmDelete = Some p.Id
    Grid.create [
        Grid.columnDefinitions "*,Auto"
        Grid.margin (Thickness(16.0, 0.0, 16.0, 10.0))
        Grid.children [
            Button.create [
                Grid.column 0
                Button.horizontalAlignment HorizontalAlignment.Stretch
                Button.horizontalContentAlignment HorizontalAlignment.Left
                Button.background Palette.surface
                Button.cornerRadius (16.0, 0.0, 0.0, 16.0)
                Button.padding (Thickness(18.0, 16.0))
                Button.onClick ((fun _ -> dispatch (OpenPaper p)), SubPatchOptions.OnChangeOf(p.Id, p.LastSegment))
                Button.content (
                    StackPanel.create [
                        StackPanel.spacing 6.0
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text p.Title
                                TextBlock.fontSize 17.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground Palette.text
                                TextBlock.textWrapping TextWrapping.Wrap
                                TextBlock.maxLines 3
                                TextBlock.textTrimming TextTrimming.CharacterEllipsis
                            ]
                            label (sizeLabel p + progress) 13.0 Palette.muted
                        ]
                    ]
                )
            ]
            Button.create [
                Grid.column 1
                Button.verticalAlignment VerticalAlignment.Stretch
                Button.width (if confirming then 96.0 else 56.0)
                Button.background (if confirming then "#5A2A2A" else Palette.surface)
                Button.cornerRadius (0.0, 16.0, 16.0, 0.0)
                Button.borderThickness (Thickness(1.0, 0.0, 0.0, 0.0))
                Button.borderBrush Palette.bg
                Button.horizontalContentAlignment HorizontalAlignment.Center
                Button.verticalContentAlignment VerticalAlignment.Center
                Button.onClick ((fun _ -> dispatch (if confirming then DeletePaper p.Id else AskDelete p.Id)), SubPatchOptions.OnChangeOf(p.Id, confirming))
                Button.content (
                    if confirming then label "Remove" 14.0 Palette.danger
                    else icon Icons.trash Palette.muted 20.0 false
                )
            ]
        ]
    ]

let private keyHint (dispatch: Msg -> unit) : IView =
    Border.create [
        Border.margin (Thickness(16.0, 0.0, 16.0, 16.0))
        Border.padding 16.0
        Border.cornerRadius 16.0
        Border.background "#1A2436"
        Border.child (
            StackPanel.create [
                StackPanel.spacing 10.0
                StackPanel.children [
                    label "Add your Mistral API key for the best experience" 15.0 Palette.text
                    label (
                        if (Services.get ()).SystemSpeech.IsSome then
                            "Mistral rewrites the paper for listening, explains every equation out loud, and reads it with a natural voice. Without a key, the device's own voice reads the text directly."
                        else
                            "Mistral rewrites the paper for listening, explains every equation out loud, and reads it with a natural voice. This version has no voice of its own, so it needs a key to read aloud.") 13.0 Palette.muted
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.children [ pill "Open settings" (fun () -> dispatch (SetShowSettings true)) false ]
                    ]
                ]
            ]
        )
    ]

/// One of the two large buttons at the bottom of the library.
let private bigButton (column: int) (data: string) (text: string) (primary: bool) (onClick: unit -> unit) : IView =
    let fg = if primary then Palette.onAccent else Palette.text
    Button.create [
        Grid.column column
        Button.height 58.0
        Button.cornerRadius 29.0
        Button.horizontalAlignment HorizontalAlignment.Stretch
        Button.horizontalContentAlignment HorizontalAlignment.Center
        Button.verticalContentAlignment VerticalAlignment.Center
        Button.background (if primary then Palette.accent else Palette.surfaceHigh)
        Button.onClick ((fun _ -> onClick ()), SubPatchOptions.OnChangeOf text)
        Button.content (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 10.0
                StackPanel.children [
                    icon data (if primary then fg else Palette.accent) 20.0 false
                    TextBlock.create [
                        TextBlock.text text
                        TextBlock.fontSize 17.0
                        TextBlock.fontWeight FontWeight.SemiBold
                        TextBlock.foreground fg
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                ]
            ]
        )
    ]

/// Cards due across the library, with a way to review them.
let private dueBanner (model: Model) (dispatch: Msg -> unit) : IView =
    let due = reviewQueue model None DateTime.UtcNow |> List.length
    if due = 0 then Border.create [ Border.isVisible false ]
    else
        Border.create [
            Border.margin (Thickness(16.0, 0.0, 16.0, 12.0))
            Border.padding (Thickness(18.0, 14.0, 14.0, 14.0))
            Border.cornerRadius 16.0
            Border.background "#1A2436"
            Border.child (
                Grid.create [
                    Grid.columnDefinitions "Auto,*,Auto"
                    Grid.children [
                        Border.create [ Grid.column 0; Border.verticalAlignment VerticalAlignment.Center; Border.child (icon Icons.cards Palette.accent 22.0 false) ]
                        StackPanel.create [
                            Grid.column 1
                            StackPanel.margin (Thickness(12.0, 0.0))
                            StackPanel.verticalAlignment VerticalAlignment.Center
                            StackPanel.children [
                                label (if due = 1 then "1 thing to review" else sprintf "%d things to review" due) 16.0 Palette.text
                                label "A few minutes now keeps them for months." 12.0 Palette.muted
                            ]
                        ]
                        Border.create [ Grid.column 2; Border.verticalAlignment VerticalAlignment.Center; Border.child (pill "Review" (fun () -> dispatch (StartReview None)) true) ]
                    ]
                ]
            )
        ]

let private libraryView (model: Model) (dispatch: Msg -> unit) : IView =
    DockPanel.create [
        DockPanel.children [
            Grid.create [
                DockPanel.dock Dock.Top
                Grid.columnDefinitions "*,Auto"
                Grid.margin (Thickness(20.0, 20.0, 8.0, 16.0))
                Grid.children [
                    StackPanel.create [
                        Grid.column 0
                        StackPanel.spacing 4.0
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text "Paper Reader"
                                TextBlock.fontSize 28.0
                                TextBlock.fontWeight FontWeight.Bold
                                TextBlock.foreground Palette.text
                            ]
                            label "Listen to papers, books, articles and notes. The math appears on screen." 14.0 Palette.muted
                        ]
                    ]
                    StackPanel.create [
                        Grid.column 1
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.verticalAlignment VerticalAlignment.Top
                        StackPanel.children [
                            iconButton Icons.cards 22.0 (fun () -> dispatch OpenLearn) "learn"
                            iconButton Icons.sliders 22.0 (fun () -> dispatch (SetShowSettings true)) "settings"
                        ]
                    ]
                ]
            ]
            Grid.create [
                DockPanel.dock Dock.Bottom
                Grid.columnDefinitions "*,12,*"
                Grid.margin (Thickness(16.0, 8.0, 16.0, 16.0))
                Grid.children [
                    bigButton 0 Icons.search "Find papers" false (fun () -> dispatch OpenDiscover)
                    bigButton 2 Icons.plus "Open a document" true (fun () -> dispatch OpenDocument)
                ]
            ]
            ScrollViewer.create [
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.children [
                            if not (Settings.hasKey model.Settings) then keyHint dispatch
                            dueBanner model dispatch
                            if model.Papers.IsEmpty then
                                StackPanel.create [
                                    StackPanel.margin (Thickness(32.0, 48.0))
                                    StackPanel.spacing 10.0
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text "No papers yet"
                                            TextBlock.fontSize 20.0
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground Palette.text
                                            TextBlock.horizontalAlignment HorizontalAlignment.Center
                                        ]
                                        TextBlock.create [
                                            TextBlock.text "Find a paper to listen to, open a document (PDF, EPUB, Word, slides, web page, Markdown, text, or a photo of pages), or share one to Paper Reader from another app. Each one is prepared once and kept on this device, so it opens instantly afterwards."
                                            TextBlock.fontSize 14.0
                                            TextBlock.foreground Palette.muted
                                            TextBlock.textWrapping TextWrapping.Wrap
                                            TextBlock.textAlignment TextAlignment.Center
                                        ]
                                    ]
                                ]
                            else
                                for p in model.Papers do
                                    paperCard model p dispatch
                        ]
                    ]
                )
            ]
        ]
    ]

// ---------------------------------------------------------------------------------------------
// Find papers
// ---------------------------------------------------------------------------------------------

/// Topics offered before anything is searched, to show what is there.
let private topics =
    [ "large language models"; "diffusion models"; "reinforcement learning"; "protein structure prediction"; "CRISPR"
      "quantum error correction"; "dark matter"; "climate models"; "graph neural networks"; "causal inference"; "sleep and memory" ]

let private authorsLine (f: Discover.Found) =
    let names =
        match f.Authors with
        | [] -> None
        | [ a ] -> Some a
        | [ a; b ] -> Some(a + ", " + b)
        | a :: b :: _ :: [] -> Some(sprintf "%s, %s, %s" a b f.Authors.[2])
        | a :: _ -> Some(a + " et al.")
    [ names; f.Year |> Option.map string; f.Venue ] |> List.choose id |> String.concat " · "

let private findCard (model: Model) (f: Discover.Found) (dispatch: Msg -> unit) : IView =
    let d = model.Discover
    let expanded = d.Expanded = Some f.Key
    let fetching = d.Fetching |> Option.filter (fun (k, _, _) -> k = f.Key)
    let busyElsewhere = d.Fetching.IsSome && fetching.IsNone
    let owned = d.Known |> List.tryFind (fun (p, s) -> Discover.inLibrary [ p.Title, s ] f) |> Option.map fst
    let failure = d.Failed |> Option.filter (fun (k, _) -> k = f.Key) |> Option.map snd
    Border.create [
        Border.margin (Thickness(16.0, 0.0, 16.0, 10.0))
        Border.padding (Thickness(18.0, 16.0, 18.0, 10.0))
        Border.cornerRadius 16.0
        Border.background Palette.surface
        Border.child (
            StackPanel.create [
                StackPanel.spacing 8.0
                StackPanel.children [
                    // tapping the text shows the whole abstract
                    StackPanel.create [
                        StackPanel.spacing 6.0
                        StackPanel.background "Transparent"
                        StackPanel.onTapped ((fun _ -> dispatch (ToggleAbstract f.Key)), SubPatchOptions.OnChangeOf f.Key)
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text f.Title
                                TextBlock.fontSize 16.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground Palette.text
                                TextBlock.textWrapping TextWrapping.Wrap
                            ]
                            match authorsLine f with
                            | "" -> ()
                            | line -> label line 13.0 Palette.muted
                            match f.Abstract with
                            | Some a ->
                                TextBlock.create [
                                    TextBlock.text a
                                    TextBlock.fontSize 13.0
                                    TextBlock.lineHeight 19.0
                                    TextBlock.foreground Palette.faint
                                    TextBlock.textWrapping TextWrapping.Wrap
                                    TextBlock.maxLines (if expanded then 0 else 3)
                                    TextBlock.textTrimming (if expanded then TextTrimming.None else TextTrimming.WordEllipsis)
                                ]
                            | None -> ()
                        ]
                    ]
                    Grid.create [
                        Grid.columnDefinitions "Auto,*,Auto"
                        Grid.margin (Thickness(0.0, 4.0, 0.0, 0.0))
                        Grid.children [
                            StackPanel.create [
                                Grid.column 0
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 4.0
                                StackPanel.children [
                                    match owned, fetching with
                                    | Some p, _ -> pill "In your library · Play" (fun () -> dispatch (OpenPaper p)) false
                                    | None, Some (_, step, _) ->
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 10.0
                                            StackPanel.children [
                                                ProgressBar.create [
                                                    ProgressBar.isIndeterminate true
                                                    ProgressBar.width 28.0
                                                    ProgressBar.minWidth 28.0
                                                    ProgressBar.height 4.0
                                                    ProgressBar.minHeight 4.0
                                                    ProgressBar.foreground Palette.accent
                                                    ProgressBar.background Palette.surfaceHigh
                                                    ProgressBar.verticalAlignment VerticalAlignment.Center
                                                ]
                                                TextBlock.create [
                                                    TextBlock.text step
                                                    TextBlock.fontSize 13.0
                                                    TextBlock.foreground Palette.muted
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                ]
                                                textLink "Cancel" (fun () -> dispatch CancelFetch) "cancel-fetch"
                                            ]
                                        ]
                                    | None, None ->
                                        Button.create [
                                            Button.content "Listen"
                                            Button.fontSize 15.0
                                            Button.fontWeight FontWeight.SemiBold
                                            Button.padding (Thickness(18.0, 8.0))
                                            Button.cornerRadius 20.0
                                            Button.background Palette.accent
                                            Button.foreground Palette.onAccent
                                            Button.isEnabled (not busyElsewhere)
                                            Button.onClick ((fun _ -> dispatch (FetchPaper f)), SubPatchOptions.OnChangeOf f.Key)
                                        ]
                                    match f.Page, fetching with
                                    | Some page, None -> textLink "  Web page" (fun () -> dispatch (OpenLink page)) ("page", f.Key)
                                    | _ -> ()
                                ]
                            ]
                            TextBlock.create [
                                Grid.column 2
                                TextBlock.verticalAlignment VerticalAlignment.Center
                                TextBlock.fontSize 12.0
                                TextBlock.foreground Palette.faint
                                TextBlock.text (
                                    match f.Citations, Discover.pdfHost f with
                                    | c, _ when c > 0 -> sprintf "%s citation%s" (c.ToString("N0", Globalization.CultureInfo.InvariantCulture)) (if c = 1 then "" else "s")
                                    | _, Some h when fetching.IsNone -> h
                                    | _ -> "")
                            ]
                        ]
                    ]
                    match failure with
                    | Some e ->
                        StackPanel.create [
                            StackPanel.spacing 4.0
                            StackPanel.margin (Thickness(0.0, 0.0, 0.0, 6.0))
                            StackPanel.children [
                                label e 13.0 Palette.danger
                                label
                                    (if f.Page.IsSome then "Open the web page, download the PDF there, and share it to Paper Reader (or open it with Open a document)."
                                     else "Download it in a browser and open it with Open a document.")
                                    12.0 Palette.muted
                            ]
                        ]
                    | None -> ()
                ]
            ]
        )
    ]

let private discoverStatus (text: string) (spinning: bool) : IView =
    StackPanel.create [
        StackPanel.margin (Thickness(20.0, 8.0, 20.0, 16.0))
        StackPanel.spacing 10.0
        StackPanel.children [
            if spinning then
                ProgressBar.create [
                    ProgressBar.isIndeterminate true
                    ProgressBar.height 4.0
                    ProgressBar.minHeight 4.0
                    ProgressBar.foreground Palette.accent
                    ProgressBar.background Palette.surfaceHigh
                ]
            label text 14.0 Palette.muted
        ]
    ]

let private discoverHeading (text: string) (action: IView option) : IView =
    Grid.create [
        Grid.columnDefinitions "*,Auto"
        Grid.margin (Thickness(20.0, 14.0, 8.0, 8.0))
        Grid.children [
            TextBlock.create [
                Grid.column 0
                TextBlock.text text
                TextBlock.fontSize 13.0
                TextBlock.fontWeight FontWeight.Bold
                TextBlock.foreground Palette.accent
                TextBlock.verticalAlignment VerticalAlignment.Center
            ]
            match action with
            | Some a -> Border.create [ Grid.column 1; Border.child a ]
            | None -> ()
        ]
    ]

let private discoverView (model: Model) (dispatch: Msg -> unit) : IView =
    let d = model.Discover
    DockPanel.create [
        DockPanel.children [
            Grid.create [
                DockPanel.dock Dock.Top
                Grid.columnDefinitions "Auto,*"
                Grid.margin (Thickness(4.0, 6.0, 16.0, 0.0))
                Grid.children [
                    Border.create [ Grid.column 0; Border.child (iconButton Icons.chevronLeft 24.0 (fun () -> dispatch CloseDiscover) "close-discover") ]
                    TextBlock.create [
                        Grid.column 1
                        TextBlock.text "Find papers"
                        TextBlock.fontSize 24.0
                        TextBlock.fontWeight FontWeight.Bold
                        TextBlock.foreground Palette.text
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                ]
            ]
            Grid.create [
                DockPanel.dock Dock.Top
                Grid.columnDefinitions "*,Auto"
                Grid.margin (Thickness(16.0, 8.0, 16.0, 8.0))
                Grid.children [
                    TextBox.create [
                        Grid.column 0
                        TextBox.text d.Input
                        TextBox.watermark "Search topics, titles, authors, or paste an arXiv id, DOI or web address"
                        TextBox.fontSize 15.0
                        TextBox.cornerRadius 22.0
                        TextBox.padding (Thickness(16.0, 11.0))
                        TextBox.verticalContentAlignment VerticalAlignment.Center
                        TextBox.onTextChanged ((fun t -> if t <> model.Discover.Input then dispatch (SetDiscoverInput t)), SubPatchOptions.OnChangeOf d.Input)
                        TextBox.onKeyDown ((fun e -> if e.Key = Input.Key.Enter then e.Handled <- true; dispatch RunSearch), SubPatchOptions.Never)
                    ]
                    Button.create [
                        Grid.column 1
                        Button.width 46.0
                        Button.height 46.0
                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                        Button.cornerRadius 23.0
                        Button.background Palette.accent
                        Button.borderThickness 0.0
                        Button.horizontalContentAlignment HorizontalAlignment.Center
                        Button.verticalContentAlignment VerticalAlignment.Center
                        Button.onClick ((fun _ -> dispatch RunSearch), SubPatchOptions.Never)
                        Button.content (icon Icons.search Palette.onAccent 20.0 false)
                    ]
                ]
            ]
            ScrollViewer.create [
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.margin (Thickness(0.0, 0.0, 0.0, 24.0))
                        StackPanel.children [
                            match d.Search with
                            | Some s ->
                                discoverHeading
                                    (if s.Loading && s.Results.IsEmpty then "SEARCHING"
                                     elif s.Total > s.Results.Length then sprintf "%s FREE PAPERS" (s.Total.ToString("N0", Globalization.CultureInfo.InvariantCulture))
                                     elif s.Results.Length = 1 then "1 PAPER"
                                     else sprintf "%d PAPERS" s.Results.Length)
                                    (Some(textLink "Clear" (fun () -> dispatch ClearSearch) "clear-search"))
                                for f in s.Results do
                                    findCard model f dispatch
                                match s.Error with
                                | Some e -> discoverStatus e false
                                | None when s.Loading -> discoverStatus "Searching OpenAlex" true
                                | None when s.Results.IsEmpty ->
                                    discoverStatus "Nothing with a free PDF matched. Try other words, or paste an arXiv id or DOI." false
                                | None when s.Results.Length < s.Total ->
                                    StackPanel.create [
                                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                                        StackPanel.margin (Thickness(0.0, 6.0))
                                        StackPanel.children [ pill "Show more" (fun () -> dispatch SearchMore) false ]
                                    ]
                                | None -> ()
                            | None ->
                                discoverHeading "RECOMMENDED FOR YOU"
                                    (match d.Recs with
                                     | Recs.Ready (_, _ :: _) | Recs.Failed _ -> Some(iconButton Icons.refresh 18.0 (fun () -> dispatch (LoadRecs true)) "refresh-recs")
                                     | _ -> None)
                                match d.Recs with
                                | Recs.NotLoaded -> ()
                                | Recs.Loading step -> discoverStatus step true
                                | Recs.Failed e -> discoverStatus ("Couldn't get recommendations: " + e) false
                                | Recs.Ready ([], []) ->
                                    discoverStatus "Once you have listened to a few papers, papers like them show up here. Search, or pick a topic below to start." false
                                | Recs.Ready ([], _) ->
                                    discoverStatus "No recommendations yet: the papers in your library weren't found in the paper databases. Search instead, or pick a topic below." false
                                | Recs.Ready (recs, seeds) ->
                                    label (sprintf "New and related papers with a free PDF, based on the %s in your library." (if seeds.Length = 1 then "paper" else sprintf "%d papers" seeds.Length)) 13.0 Palette.muted
                                    |> fun l -> Border.create [ Border.margin (Thickness(20.0, 0.0, 20.0, 12.0)); Border.child l ]
                                    for f in recs |> List.truncate 30 do
                                        findCard model f dispatch
                                discoverHeading "BROWSE A TOPIC" None
                                WrapPanel.create [
                                    WrapPanel.margin (Thickness(16.0, 0.0, 16.0, 0.0))
                                    WrapPanel.children [ for t in topics -> chip t (fun () -> dispatch (SearchTopic t)) ]
                                ]
                            label "Search by OpenAlex, recommendations by Semantic Scholar. Only papers with a free PDF are listed; some publishers only let a browser download them."
                                12.0 Palette.faint
                            |> fun l -> Border.create [ Border.margin (Thickness(20.0, 16.0, 20.0, 0.0)); Border.child l ]
                        ]
                    ]
                )
            ]
        ]
    ]

// ---------------------------------------------------------------------------------------------
// Importing
// ---------------------------------------------------------------------------------------------

let private importingView (s: ImportState) (dispatch: Msg -> unit) : IView =
    StackPanel.create [
        StackPanel.verticalAlignment VerticalAlignment.Center
        StackPanel.margin (Thickness(32.0, 0.0))
        StackPanel.spacing 14.0
        StackPanel.children [
            TextBlock.create [
                TextBlock.text "Preparing your paper"
                TextBlock.fontSize 24.0
                TextBlock.fontWeight FontWeight.Bold
                TextBlock.foreground Palette.text
            ]
            label s.Name 14.0 Palette.muted
            ProgressBar.create [
                ProgressBar.height 6.0
                ProgressBar.minHeight 6.0
                ProgressBar.cornerRadius 3.0
                ProgressBar.foreground Palette.accent
                ProgressBar.background Palette.surfaceHigh
                ProgressBar.minimum 0.0
                ProgressBar.maximum 1.0
                match s.Progress with
                | Some v ->
                    ProgressBar.isIndeterminate false
                    ProgressBar.value v
                | None -> ProgressBar.isIndeterminate true
            ]
            label s.Step 15.0 Palette.text
            label "This happens once. The narration, equation images and voice are cached on this device." 13.0 Palette.faint
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.children [ pill "Cancel" (fun () -> dispatch CancelImport) false ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------------------------
// Reader
// ---------------------------------------------------------------------------------------------

let private visualName (v: Visual) =
    match v.Kind with
    | VisualKind.Inline -> "From the text"
    | _ -> Help.visualName v

let private visualCaption (script: Script) (seg: Segment) (v: Visual) =
    let name = visualName v
    let where = sprintf "page %d" (v.Page + 1)
    match seg.Reason with
    | ShowReason.Own -> sprintf "%s · %s" name where
    | ShowReason.Reference -> sprintf "%s · referred to now · %s" name where
    | ShowReason.Recent -> sprintf "%s · still discussed · %s" name where

let private remainingMs (r: ReaderState) (speed: float) =
    let segs = r.Script.Segments
    let mutable total = 0
    for i in r.Current .. segs.Length - 1 do
        total <-
            total
            + (match r.Durations.TryFind i with
               | Some d -> d
               | None -> Timeline.estimateMs segs.[i].Say + segs.[i].PauseAfterMs)
    int (float (total - r.Offset) / speed)

/// Display size of a crop pixel, in dp.
let private mathScale = 0.9

/// The segment where an equation is first read, for "hear it again".
let private firstReading (script: Script) (id: string) =
    Narration.equationOrder script |> Array.tryFind (fun (v, _) -> v.Id = id) |> Option.map snd

let private stage (r: ReaderState) (seg: Segment) (dispatch: Msg -> unit) : IView =
    let paths = Store.Paths((Services.get ()).DataDir)
    let visual = (match r.Held with Some v -> Some v | None -> seg.Show) |> Option.bind r.Script.Visual
    let image = visual |> Option.bind (fun v -> bitmap (paths.Image(r.Paper.Id, v.Id)) |> Option.map (fun b -> v, b))
    match image with
    | Some (v, bmp) ->
        // math mode: the image fills the stage, the spoken sentence is the caption underneath
        Grid.create [
            Grid.rowDefinitions "*,Auto"
            Grid.children [
                Border.create [
                    Grid.row 0
                    Border.margin (Thickness(14.0, 6.0, 14.0, 10.0))
                    Border.padding (Thickness(14.0, 10.0, 14.0, 14.0))
                    Border.cornerRadius 18.0
                    Border.background Palette.paper
                    Border.verticalAlignment VerticalAlignment.Center
                    Border.onTapped ((fun _ -> dispatch ToggleZoom), SubPatchOptions.Never)
                    Border.child (
                        Grid.create [
                            Grid.rowDefinitions "Auto,*"
                            Grid.children [
                                Grid.create [
                                    Grid.row 0
                                    Grid.columnDefinitions "*,Auto"
                                    Grid.margin (Thickness(0.0, 0.0, 0.0, 8.0))
                                    Grid.children [
                                        TextBlock.create [
                                            Grid.column 0
                                            TextBlock.text (
                                                if r.Held.IsSome then sprintf "%s · page %d · stopped here" (visualName v) (v.Page + 1)
                                                else visualCaption r.Script seg v)
                                            TextBlock.fontSize 12.0
                                            TextBlock.foreground Palette.ink
                                            TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                        ]
                                        Border.create [ Grid.column 1; Border.child (icon Icons.expand Palette.ink 16.0 false) ]
                                    ]
                                ]
                                Image.create [
                                    Grid.row 1
                                    Image.source bmp
                                    Image.stretch Stretch.Uniform
                                    // one size for all math: crops are 3 px per point, so 10 pt text shows at
                                    // about 27 dp; only crops too big for the card are scaled down
                                    Image.maxWidth (float bmp.PixelSize.Width * mathScale)
                                    Image.maxHeight (float bmp.PixelSize.Height * mathScale)
                                    Image.horizontalAlignment HorizontalAlignment.Center
                                    Image.verticalAlignment VerticalAlignment.Center
                                ]
                            ]
                        ]
                    )
                ]
                if r.Held.IsSome then
                    // stopped at an equation: take time with it, then carry on from the next sentence
                    StackPanel.create [
                        Grid.row 1
                        StackPanel.margin (Thickness(22.0, 0.0, 22.0, 14.0))
                        StackPanel.spacing 12.0
                        StackPanel.children [
                            label "Take your time with it. Continue when you're ready." 16.0 Palette.muted
                            StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 10.0
                                StackPanel.children [
                                    pill "Continue" (fun () -> dispatch TogglePlay) true
                                    match firstReading r.Script v.Id with
                                    | Some i -> pill "Hear it again" (fun () -> dispatch (JumpToSegment i)) false
                                    | None -> ()
                                    pill "Ask" (fun () -> dispatch (OpenHelp(Some v.Id))) false
                                ]
                            ]
                        ]
                    ]
                else
                Border.create [
                    Grid.row 1
                    Border.padding (Thickness(22.0, 0.0, 22.0, 14.0))
                    Border.child (
                        TextBlock.create [
                            TextBlock.text seg.Say
                            TextBlock.fontSize 18.0
                            TextBlock.lineHeight 26.0
                            TextBlock.foreground Palette.text
                            TextBlock.textWrapping TextWrapping.Wrap
                            TextBlock.maxLines 6
                            TextBlock.textTrimming TextTrimming.WordEllipsis
                        ]
                    )
                ]
            ]
        ]
    | None ->
        // reading mode: large text of the sentence being spoken, the previous one faded above it
        let prev = if r.Current > 0 then Some r.Script.Segments.[r.Current - 1] else None
        let heading = seg.Kind = UnitKind.Heading || seg.Kind = UnitKind.Title
        ScrollViewer.create [
            ScrollViewer.content (
                StackPanel.create [
                    StackPanel.margin (Thickness(24.0, 24.0, 24.0, 16.0))
                    StackPanel.verticalAlignment VerticalAlignment.Center
                    StackPanel.spacing 18.0
                    StackPanel.children [
                        match prev with
                        | Some p ->
                            plainButton "Transparent" [
                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                Button.horizontalContentAlignment HorizontalAlignment.Left
                                Button.padding 0.0
                                Button.onClick ((fun _ -> dispatch (JumpToSegment(r.Current - 1))), SubPatchOptions.OnChangeOf r.Current)
                                Button.content (
                                    TextBlock.create [
                                        TextBlock.text p.Say
                                        TextBlock.fontSize 16.0
                                        TextBlock.lineHeight 23.0
                                        TextBlock.foreground Palette.faint
                                        TextBlock.textWrapping TextWrapping.Wrap
                                        TextBlock.maxLines 3
                                        TextBlock.textTrimming TextTrimming.WordEllipsis
                                    ]
                                )
                            ]
                        | None -> ()
                        TextBlock.create [
                            TextBlock.text seg.Say
                            TextBlock.fontSize (if heading then 30.0 else 25.0)
                            TextBlock.lineHeight (if heading then 38.0 else 35.0)
                            TextBlock.fontWeight (if heading then FontWeight.Bold else FontWeight.Medium)
                            TextBlock.foreground (if heading then Palette.accent else Palette.text)
                            TextBlock.textWrapping TextWrapping.Wrap
                        ]
                    ]
                ]
            )
        ]

let private roundControl (data: string) (onClick: unit -> unit) (caption: string) : IView =
    Grid.create [
        Grid.children [
            plainButton "Transparent" [
                Button.width 64.0
                Button.height 64.0
                Button.padding 0.0
                Button.horizontalContentAlignment HorizontalAlignment.Center
                Button.verticalContentAlignment VerticalAlignment.Center
                Button.cornerRadius 32.0
                Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Never)
                Button.content (icon data Palette.text 40.0 false)
            ]
            TextBlock.create [
                TextBlock.text caption
                TextBlock.fontSize 11.0
                TextBlock.fontWeight FontWeight.Bold
                TextBlock.foreground Palette.text
                TextBlock.horizontalAlignment HorizontalAlignment.Center
                TextBlock.verticalAlignment VerticalAlignment.Center
                TextBlock.margin (Thickness(0.0, 3.0, 0.0, 0.0))
                TextBlock.isHitTestVisible false
            ]
        ]
    ]

/// A playback error (with a way out) or "preparing the voice", when there is one.
let private playerStatus (model: Model) (r: ReaderState) (dispatch: Msg -> unit) : IView list =
    let usingMistralVoice = Settings.hasKey model.Settings && model.Settings.UseMistralVoice
    match r.Error, r.Waiting with
    | Some e, _ ->
        [ StackPanel.create [
              StackPanel.spacing 8.0
              StackPanel.children [
                  label e 13.0 Palette.danger
                  if usingMistralVoice then
                      StackPanel.create [
                          StackPanel.orientation Orientation.Horizontal
                          StackPanel.spacing 8.0
                          StackPanel.children [
                              pill "Use the device's voice" (fun () -> dispatch UsePhoneVoice) false
                              pill "Settings" (fun () -> dispatch (SetShowSettings true)) false
                          ]
                      ]
              ]
          ] ]
    | None, true ->
        [ StackPanel.create [
              StackPanel.orientation Orientation.Horizontal
              StackPanel.spacing 10.0
              StackPanel.children [
                  ProgressBar.create [
                      ProgressBar.isIndeterminate true
                      ProgressBar.width 48.0
                      ProgressBar.height 4.0
                      ProgressBar.minHeight 4.0
                      ProgressBar.minWidth 48.0
                      ProgressBar.verticalAlignment VerticalAlignment.Center
                      ProgressBar.foreground Palette.accent
                  ]
                  label "Preparing the voice…" 13.0 Palette.muted
              ]
          ] ]
    | None, false -> []

/// How far through the paper the listener is, as a thin bar.
let private progressBar (r: ReaderState) : IView =
    let segs = r.Script.Segments
    let fraction =
        let d = r.Durations.TryFind r.Current |> Option.defaultValue 1
        (float r.Current + min 1.0 (float r.Offset / float (max 1 d))) / float (max 1 segs.Length)
    ProgressBar.create [
        ProgressBar.minimum 0.0
        ProgressBar.maximum 1.0
        ProgressBar.value fraction
        ProgressBar.height 4.0
        ProgressBar.minHeight 4.0
        ProgressBar.cornerRadius 2.0
        ProgressBar.foreground Palette.accent
        ProgressBar.background Palette.surfaceHigh
    ]

let private controls (model: Model) (r: ReaderState) (dispatch: Msg -> unit) : IView =
    let seg = r.Script.Segments.[r.Current]
    let sectionCount = max 1 (r.Script.Sections.Length - 1)
    Border.create [
        Border.background Palette.surface
        Border.cornerRadius (24.0, 24.0, 0.0, 0.0)
        Border.padding (Thickness(18.0, 14.0, 18.0, 18.0))
        Border.child (
            StackPanel.create [
                StackPanel.spacing 10.0
                StackPanel.children [
                    yield! playerStatus model r dispatch
                    progressBar r
                    Grid.create [
                        Grid.columnDefinitions "*,Auto"
                        Grid.children [
                            TextBlock.create [
                                Grid.column 0
                                TextBlock.text (
                                    if r.Finished then "Finished"
                                    else sprintf "Section %d of %d · %s %d of %d" (max 1 seg.Section) sectionCount (Formats.pageNoun r.Paper.Format) (seg.Page + 1) r.Script.PageCount)
                                TextBlock.fontSize 12.0
                                TextBlock.foreground Palette.muted
                            ]
                            TextBlock.create [
                                Grid.column 1
                                TextBlock.text (formatMinutes (remainingMs r model.Settings.Speed))
                                TextBlock.fontSize 12.0
                                TextBlock.foreground Palette.muted
                            ]
                        ]
                    ]
                    Grid.create [
                        Grid.columnDefinitions "*,Auto,Auto,Auto,*"
                        Grid.children [
                            Button.create [
                                Grid.column 0
                                Button.horizontalAlignment HorizontalAlignment.Left
                                Button.verticalAlignment VerticalAlignment.Center
                                Button.minWidth 60.0
                                Button.height 40.0
                                Button.cornerRadius 20.0
                                Button.background Palette.surfaceHigh
                                Button.foreground Palette.text
                                Button.horizontalContentAlignment HorizontalAlignment.Center
                                Button.fontWeight FontWeight.SemiBold
                                Button.content (sprintf "%g×" model.Settings.Speed)
                                Button.onClick (fun _ -> dispatch CycleSpeed)
                            ]
                            Border.create [ Grid.column 1; Border.child (roundControl Icons.back (fun () -> dispatch Back15) "15") ]
                            Button.create [
                                Grid.column 2
                                Button.margin (Thickness(14.0, 0.0))
                                Button.width 76.0
                                Button.height 76.0
                                Button.cornerRadius 38.0
                                Button.background Palette.accent
                                Button.horizontalContentAlignment HorizontalAlignment.Center
                                Button.verticalContentAlignment VerticalAlignment.Center
                                Button.onClick (fun _ -> dispatch TogglePlay)
                                Button.content (icon (if r.Playing then Icons.pause else Icons.play) Palette.onAccent 34.0 true)
                            ]
                            Border.create [ Grid.column 3; Border.child (roundControl Icons.forward (fun () -> dispatch Forward15) "15") ]
                            Border.create [
                                Grid.column 4
                                Border.horizontalAlignment HorizontalAlignment.Right
                                Border.child (iconButton Icons.list 22.0 (fun () -> dispatch ToggleOutline) "outline")
                            ]
                        ]
                    ]
                ]
            ]
        )
    ]

let private outlineOverlay (r: ReaderState) (dispatch: Msg -> unit) : IView =
    let currentSection = r.Script.Segments.[r.Current].Section
    Border.create [
        Border.background Palette.bg
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "*,Auto"
                        Grid.margin (Thickness(20.0, 12.0, 8.0, 8.0))
                        Grid.children [
                            TextBlock.create [
                                Grid.column 0
                                TextBlock.text "Contents"
                                TextBlock.fontSize 22.0
                                TextBlock.fontWeight FontWeight.Bold
                                TextBlock.foreground Palette.text
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                            Border.create [ Grid.column 1; Border.child (iconButton Icons.close 22.0 (fun () -> dispatch ToggleOutline) "close-outline") ]
                        ]
                    ]
                    ScrollViewer.create [
                        ScrollViewer.content (
                            StackPanel.create [
                                StackPanel.margin (Thickness(12.0, 0.0, 12.0, 24.0))
                                StackPanel.children [
                                    for i, s in Array.indexed r.Script.Sections do
                                        let active = i = currentSection
                                        let depth = if i = 0 then 0 else (s.Title.Split(' ').[0] |> Seq.filter ((=) '.') |> Seq.length |> fun d -> if s.Title.Split(' ').[0].EndsWith "." then d - 1 else d)
                                        plainButton (if active then Palette.surfaceHigh else "Transparent") [
                                            Button.horizontalAlignment HorizontalAlignment.Stretch
                                            Button.horizontalContentAlignment HorizontalAlignment.Left
                                            Button.padding (Thickness(12.0 + 16.0 * float (max 0 depth), 12.0, 12.0, 12.0))
                                            Button.cornerRadius 12.0
                                            Button.onClick ((fun _ -> dispatch (JumpToSegment s.FirstSegment)), SubPatchOptions.OnChangeOf(i, s.FirstSegment))
                                            Button.content (
                                                TextBlock.create [
                                                    TextBlock.text (if i = 0 then "Start: " + s.Title else s.Title)
                                                    TextBlock.fontSize (if depth = 0 then 16.0 else 15.0)
                                                    TextBlock.fontWeight (if depth = 0 then FontWeight.SemiBold else FontWeight.Normal)
                                                    TextBlock.foreground (if active then Palette.accent else Palette.text)
                                                    TextBlock.textWrapping TextWrapping.Wrap
                                                ]
                                            )
                                        ]
                                ]
                            ]
                        )
                    ]
                ]
            ]
        )
    ]

let private sectionLabel (text: string) : IView =
    TextBlock.create [
        TextBlock.text text
        TextBlock.fontSize 12.0
        TextBlock.fontWeight FontWeight.Bold
        TextBlock.foreground Palette.accent
        TextBlock.margin (Thickness(4.0, 8.0, 0.0, 0.0))
    ]

let private equationCard (r: ReaderState) (v: Visual) (firstSegment: int) (ahead: bool) (dispatch: Msg -> unit) : IView =
    let paths = Store.Paths((Services.get ()).DataDir)
    StackPanel.create [
        StackPanel.spacing 8.0
        StackPanel.opacity (if ahead then 0.55 else 1.0)
        StackPanel.children [
            Border.create [
                Border.background Palette.paper
                Border.cornerRadius 14.0
                Border.padding (Thickness(12.0, 8.0, 12.0, 12.0))
                Border.onTapped ((fun _ -> dispatch (ZoomVisual v.Id)), SubPatchOptions.OnChangeOf v.Id)
                Border.child (
                    StackPanel.create [
                        StackPanel.spacing 6.0
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text (sprintf "%s · page %d" (visualName v) (v.Page + 1))
                                TextBlock.fontSize 12.0
                                TextBlock.foreground Palette.ink
                            ]
                            match bitmap (paths.Image(r.Paper.Id, v.Id)) with
                            | Some bmp ->
                                Image.create [
                                    Image.source bmp
                                    Image.stretch Stretch.Uniform
                                    Image.maxWidth (float bmp.PixelSize.Width * 0.7)
                                    Image.maxHeight (min 420.0 (float bmp.PixelSize.Height * 0.7))
                                    Image.horizontalAlignment HorizontalAlignment.Left
                                ]
                            | None -> label "(image missing)" 13.0 Palette.ink
                        ]
                    ]
                )
            ]
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 18.0
                StackPanel.children [
                    plainButton "Transparent" [
                        Button.padding (Thickness(4.0, 2.0))
                        Button.foreground Palette.accent
                        Button.fontSize 14.0
                        Button.content (if ahead then "Skip ahead to it" else "Listen from here")
                        Button.onClick ((fun _ -> dispatch (JumpToSegment firstSegment)), SubPatchOptions.OnChangeOf firstSegment)
                    ]
                    plainButton "Transparent" [
                        Button.padding (Thickness(4.0, 2.0))
                        Button.foreground Palette.accent
                        Button.fontSize 14.0
                        Button.content "Ask about it"
                        Button.onClick ((fun _ -> dispatch (OpenHelp(Some v.Id))), SubPatchOptions.OnChangeOf v.Id)
                    ]
                    plainButton "Transparent" [
                        Button.padding (Thickness(4.0, 2.0))
                        Button.foreground Palette.accent
                        Button.fontSize 14.0
                        Button.content "Remember"
                        Button.onClick ((fun _ -> dispatch (OpenCards(Some v.Id))), SubPatchOptions.OnChangeOf v.Id)
                    ]
                ]
            ]
        ]
    ]

/// Every display equation, algorithm, figure and table: the ones heard so far (latest first), then the ones coming up.
let private equationsOverlay (r: ReaderState) (dispatch: Msg -> unit) : IView =
    let order = Narration.equationOrder r.Script
    let heard = order |> Array.filter (fun (_, i) -> i <= r.Current) |> Array.rev
    let ahead = order |> Array.filter (fun (_, i) -> i > r.Current)
    Border.create [
        Border.background Palette.bg
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "*,Auto"
                        Grid.margin (Thickness(20.0, 12.0, 8.0, 8.0))
                        Grid.children [
                            TextBlock.create [
                                Grid.column 0
                                TextBlock.text "Equations and figures"
                                TextBlock.fontSize 22.0
                                TextBlock.fontWeight FontWeight.Bold
                                TextBlock.foreground Palette.text
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                            Border.create [ Grid.column 1; Border.child (iconButton Icons.close 22.0 (fun () -> dispatch ToggleEquations) "close-equations") ]
                        ]
                    ]
                    ScrollViewer.create [
                        ScrollViewer.content (
                            StackPanel.create [
                                StackPanel.margin (Thickness(16.0, 0.0, 16.0, 24.0))
                                StackPanel.spacing 14.0
                                StackPanel.children [
                                    if order.Length = 0 then label "This paper has no display equations, figures or tables." 15.0 Palette.muted
                                    if heard.Length > 0 then sectionLabel "HEARD SO FAR, LATEST FIRST"
                                    for (v, i) in heard do equationCard r v i false dispatch
                                    if ahead.Length > 0 then sectionLabel "COMING UP"
                                    for (v, i) in ahead do equationCard r v i true dispatch
                                ]
                            ]
                        )
                    ]
                ]
            ]
        )
    ]

let private zoomOverlay (r: ReaderState) (visual: string) (dispatch: Msg -> unit) : IView =
    let paths = Store.Paths((Services.get ()).DataDir)
    let bmp = bitmap (paths.Image(r.Paper.Id, visual))
    Grid.create [
        Grid.background "#F2000000"
        Grid.children [
            match bmp with
            | Some b ->
                // shown at the crop's natural resolution (about 3x the paper's size), scroll to pan
                ScrollViewer.create [
                    ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Auto
                    ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Auto
                    ScrollViewer.content (
                        Border.create [
                            Border.background Palette.paper
                            Border.padding 16.0
                            Border.margin (Thickness(12.0, 72.0, 12.0, 24.0))
                            Border.cornerRadius 12.0
                            Border.horizontalAlignment HorizontalAlignment.Left
                            Border.child (
                                Image.create [
                                    Image.source b
                                    Image.stretch Stretch.None
                                ]
                            )
                        ]
                    )
                ]
            | None -> ()
            Border.create [
                Border.horizontalAlignment HorizontalAlignment.Right
                Border.verticalAlignment VerticalAlignment.Top
                Border.margin 8.0
                Border.background Palette.surfaceHigh
                Border.cornerRadius 26.0
                Border.child (iconButton Icons.close 22.0 (fun () -> dispatch ToggleZoom) "close-zoom")
            ]
        ]
    ]


// ---------------------------------------------------------------------------------------------
// Ask
// ---------------------------------------------------------------------------------------------

/// Math in answers, typeset light on the dark background: (image, depth below the baseline in dp).
let private mathImages = Dictionary<string, (Bitmap * float) option>()

/// Pixels per dp the math is drawn at, for sharp text on dense screens.
let private mathDensity = 3.0

let private mathImage (latex: string) (display: bool) (emDp: float) : (Bitmap * float) option =
    let key = sprintf "%b|%g|%s" display emDp latex
    match mathImages.TryGetValue key with
    | true, b -> b
    | _ ->
        let made =
            try
                let painter = CSharpMath.SkiaSharp.MathPainter(FontSize = float32 (emDp * mathDensity), TextColor = SkiaSharp.SKColor.Parse Palette.text)
                painter.LineStyle <- (if display then CSharpMath.Atom.LineStyle.Display else CSharpMath.Atom.LineStyle.Text)
                painter.LaTeX <- latex
                if not (isNull painter.ErrorMessage) then None
                else
                    let r = painter.Measure(0.0f)
                    let pad = 2.0f
                    let w, h = int (ceil (r.Width + 2.0f * pad + 4.0f)), int (ceil (r.Height + 2.0f * pad))
                    if w <= 0 || h <= 0 then None
                    else
                        use bmp = new SkiaSharp.SKBitmap(w, h)
                        do
                            use c = new SkiaSharp.SKCanvas(bmp)
                            c.Clear SkiaSharp.SKColors.Transparent
                            painter.Draw(c, pad - r.X, pad - r.Y)
                        use data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)
                        use ms = new MemoryStream(data.ToArray())
                        // r.Y is the top relative to the baseline, so the part below it is the rest of the height
                        let depth = float (r.Height + r.Y + pad) / mathDensity
                        Some(new Bitmap(ms), depth)
            with _ -> None
        mathImages.[key] <- made
        made

let private mathControl (bmp: Bitmap) (depth: float) (inline': bool) : IView =
    Image.create [
        Image.source bmp
        Image.stretch Stretch.Fill
        // drawn at 3x and scaled down: thin strokes (a minus, a fraction bar) vanish without proper filtering
        Image.init (fun i -> RenderOptions.SetBitmapInterpolationMode(i, BitmapInterpolationMode.HighQuality))
        Image.width (float bmp.PixelSize.Width / mathDensity)
        Image.height (float bmp.PixelSize.Height / mathDensity)
        // inline math sits on the text's baseline, with its descenders below it
        if inline' then Image.margin (Thickness(1.0, 0.0, 1.0, -depth))
        else Image.horizontalAlignment HorizontalAlignment.Center
    ]

let private emphasisRx = Text.RegularExpressions.Regex(@"\*\*(.+?)\*\*|(?<![\w*])\*(?![\s*])(.+?)(?<![\s*])\*(?![\w*])")

/// A paragraph with **bold** and *italic* spans and inline $math$.
let private richParagraph (text: string) (size: float) (color: string) : IView =
    // (text, bold, italic) spans
    let spans =
        [ let mutable last = 0
          for m in emphasisRx.Matches text do
              if m.Index > last then yield text.Substring(last, m.Index - last), false, false
              if m.Groups.[1].Success then yield m.Groups.[1].Value, true, false
              else yield m.Groups.[2].Value, false, true
              last <- m.Index + m.Length
          if last < text.Length then yield text.Substring last, false, false ]
    TextBlock.create [
        TextBlock.fontSize size
        TextBlock.lineHeight (size * 1.5)
        TextBlock.foreground color
        TextBlock.textWrapping TextWrapping.Wrap
        TextBlock.inlines [
            for span, bold, italic in spans do
                let math = Text.RegularExpressions.Regex.Split(span, @"\$([^$\n]+)\$")
                for j in 0 .. math.Length - 1 do
                    if math.[j] <> "" then
                        match (if j % 2 = 1 then mathImage math.[j] false size else None) with
                        | Some (bmp, depth) ->
                            InlineUIContainer.create [ InlineUIContainer.child (mathControl bmp depth true) ] :> IView
                        | None ->
                            Run.create [
                                Run.text math.[j]
                                if bold then Run.fontWeight FontWeight.SemiBold
                                if italic then Run.fontStyle FontStyle.Italic
                            ]
                            :> IView
        ]
    ]

/// Paragraphs, bullet lists and formulas.
let private richText (text: string) (size: float) (color: string) : IView =
    StackPanel.create [
        StackPanel.spacing 10.0
        StackPanel.children [
            for piece in Help.pieces text do
                match piece with
                | Help.Piece.Prose text ->
                    for para in Text.RegularExpressions.Regex.Split(text, @"\n\s*\n") do
                        let para = Text.RegularExpressions.Regex.Replace(para.Trim(), @"(?m)^\s*[-*]\s+", "• ")
                        if para <> "" then richParagraph para size color
                | Help.Piece.Formula latex ->
                    match mathImage latex true (size + 1.0) with
                    | Some (bmp, depth) ->
                        ScrollViewer.create [
                            ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Auto
                            ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Disabled
                            ScrollViewer.content (mathControl bmp depth false)
                        ]
                    | None -> label latex 15.0 Palette.muted
        ]
    ]

/// An answer in Ask.
let private answerBody (answer: string) : IView = richText answer 16.0 Palette.text

/// A small image of an equation or figure, tapped to see it full size.
let private visualThumb (r: ReaderState) (id: string) (maxHeight: float) (dispatch: Msg -> unit) : IView =
    let paths = Store.Paths((Services.get ()).DataDir)
    match r.Script.Visual id, bitmap (paths.Image(r.Paper.Id, id)) with
    | Some v, Some bmp ->
        Border.create [
            Border.background Palette.paper
            Border.cornerRadius 12.0
            Border.padding (Thickness(10.0, 6.0, 10.0, 10.0))
            Border.horizontalAlignment HorizontalAlignment.Left
            Border.onTapped ((fun _ -> dispatch (ZoomVisual id)), SubPatchOptions.OnChangeOf id)
            Border.child (
                StackPanel.create [
                    StackPanel.spacing 4.0
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text (sprintf "%s · page %d" (visualName v) (v.Page + 1))
                            TextBlock.fontSize 11.0
                            TextBlock.foreground Palette.ink
                        ]
                        Image.create [
                            Image.source bmp
                            Image.stretch Stretch.Uniform
                            Image.maxWidth (float bmp.PixelSize.Width * 0.6)
                            Image.maxHeight (min maxHeight (float bmp.PixelSize.Height * 0.6))
                            Image.horizontalAlignment HorizontalAlignment.Left
                        ]
                    ]
                ]
            )
        ]
    | _ -> Border.create []

/// One question and its answer.
let private turnView (model: Model) (r: ReaderState) (h: HelpState) (t: HelpTurn) (earlier: bool) (dispatch: Msg -> unit) : IView =
    let speaking = h.Speaking = Some t.AskedUtc
    let making = model.Making |> Option.exists (fun m -> m.PaperId = r.Paper.Id && m.What = t.Question)
    let made =
        match model.Made with
        | Some (p, cards) when p = r.Paper.Id -> cards |> List.filter (fun c -> c.Origin = t.Question) |> List.length
        | _ -> 0
    StackPanel.create [
        StackPanel.spacing 10.0
        StackPanel.children [
            label t.Question 15.0 Palette.accent
            answerBody t.Answer
            match t.Show with
            | Some v when t.About <> Some v -> visualThumb r v 180.0 dispatch
            | _ -> ()
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.children [
                    textLink (if speaking then "Stop reading" else "Read it to me") (fun () -> dispatch (if speaking then StopSpeaking else SpeakAnswer t)) (t.AskedUtc, speaking)
                    if earlier then textLink "Listen from there" (fun () -> dispatch (JumpToSegment(max 0 (t.Segment - 1)))) t.AskedUtc
                    if making then label "Making a card…" 14.0 Palette.muted
                    elif made > 0 then label (if made = 1 then "Card added" else sprintf "%d cards added" made) 14.0 Palette.muted
                    else textLink "Make a card" (fun () -> dispatch (MakeCards(r.Paper.Id, Cards.Request.Answer t))) ("card", t.AskedUtc)
                ]
            ]
        ]
    ]

let private helpOverlay (model: Model) (r: ReaderState) (h: HelpState) (dispatch: Msg -> unit) : IView =
    let about = h.About |> Option.bind r.Script.Visual
    let seg = r.Script.Segments.[max 0 (min h.Position (r.Script.Segments.Length - 1))]
    let session = h.History |> List.skip (min h.Earlier h.History.Length) |> List.rev
    let earlier = h.History |> List.truncate h.Earlier |> List.rev
    let hasKey = Settings.hasKey model.Settings
    let busy = h.Pending.IsSome || h.Mic = Mic.Transcribing
    let taps =
        match session with
        | last :: _ ->
            [ for q in last.Followups -> Help.Ask.Followup q, q.Replace("$", "")
              yield Help.Ask.TellMore, "Tell me more" ]
        | [] ->
            [ for a in Help.suggestions r.Script h.Position about ->
                  let text =
                      match a with
                      | Help.Ask.Walkthrough ->
                          match about with
                          | Some v when v.Kind = VisualKind.Figure || v.Kind = VisualKind.Table -> sprintf "What does %s show?" (Help.spokenName v)
                          | Some v -> sprintf "Walk me through %s" (Help.spokenName v)
                          | None -> "Walk me through it"
                      | Help.Ask.Simpler -> "I didn't get that"
                      | Help.Ask.Example -> "Give an example"
                      | Help.Ask.WhyItMatters -> "Why does it matter?"
                      | Help.Ask.Recap -> "Recap so far"
                      | Help.Ask.Define t -> sprintf "What is %s?" t
                      | a -> Help.questionText a about
                  a, text ]
    Border.create [
        Border.background Palette.bg
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    // header
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "*,Auto"
                        Grid.margin (Thickness(20.0, 12.0, 8.0, 4.0))
                        Grid.children [
                            StackPanel.create [
                                Grid.column 0
                                StackPanel.verticalAlignment VerticalAlignment.Center
                                StackPanel.children [
                                    TextBlock.create [
                                        TextBlock.text "Ask"
                                        TextBlock.fontSize 22.0
                                        TextBlock.fontWeight FontWeight.Bold
                                        TextBlock.foreground Palette.text
                                    ]
                                    label "Answers come from the paper, about where you are in it." 12.0 Palette.muted
                                ]
                            ]
                            Border.create [ Grid.column 1; Border.child (iconButton Icons.close 22.0 (fun () -> dispatch (CloseHelp false)) "close-help") ]
                        ]
                    ]
                    // taps, typing, the microphone, and back to listening
                    Border.create [
                        DockPanel.dock Dock.Bottom
                        Border.background Palette.surface
                        Border.cornerRadius (24.0, 24.0, 0.0, 0.0)
                        Border.padding (Thickness(16.0, 14.0, 16.0, 16.0))
                        Border.child (
                            StackPanel.create [
                                StackPanel.spacing 10.0
                                StackPanel.children [
                                    match h.Error with
                                    | Some e -> label e 14.0 Palette.danger
                                    | None -> ()
                                    if hasKey && not busy then
                                        WrapPanel.create [
                                            WrapPanel.children [ for a, text in taps -> chip text (fun () -> dispatch (AskHelp(a, false))) ]
                                        ]
                                    if hasKey then
                                        Grid.create [
                                            Grid.columnDefinitions "*,Auto,Auto"
                                            Grid.children [
                                                match h.Mic with
                                                | Mic.Recording ->
                                                    label "Listening… tap the button when you're done." 15.0 Palette.text
                                                | Mic.Transcribing -> label "Getting your question…" 15.0 Palette.muted
                                                | Mic.Idle ->
                                                    TextBox.create [
                                                        Grid.column 0
                                                        TextBox.text h.Input
                                                        TextBox.watermark "Ask anything about the paper"
                                                        TextBox.fontSize 15.0
                                                        TextBox.cornerRadius 20.0
                                                        TextBox.padding (Thickness(14.0, 9.0))
                                                        TextBox.verticalContentAlignment VerticalAlignment.Center
                                                        TextBox.onTextChanged ((fun t -> if t <> h.Input then dispatch (SetHelpInput t)), SubPatchOptions.OnChangeOf h.Input)
                                                        TextBox.onKeyDown ((fun e -> if e.Key = Input.Key.Enter then e.Handled <- true; dispatch SendHelpInput), SubPatchOptions.Never)
                                                    ]
                                                if h.Mic = Mic.Idle && h.Input.Trim() <> "" then
                                                    Button.create [
                                                        Grid.column 1
                                                        Button.width 44.0
                                                        Button.height 44.0
                                                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                                        Button.cornerRadius 22.0
                                                        Button.padding 0.0
                                                        Button.background Palette.accent
                                                        Button.horizontalContentAlignment HorizontalAlignment.Center
                                                        Button.verticalContentAlignment VerticalAlignment.Center
                                                        Button.isEnabled (not busy)
                                                        Button.onClick ((fun _ -> dispatch SendHelpInput), SubPatchOptions.Never)
                                                        Button.content (icon Icons.send Palette.onAccent 20.0 false)
                                                    ]
                                                if (Services.get ()).Recorder.IsSome then
                                                    Button.create [
                                                        Grid.column 2
                                                        Button.width 44.0
                                                        Button.height 44.0
                                                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                                        Button.cornerRadius 22.0
                                                        Button.padding 0.0
                                                        Button.background (if h.Mic = Mic.Recording then Palette.danger else Palette.surfaceHigh)
                                                        Button.horizontalContentAlignment HorizontalAlignment.Center
                                                        Button.verticalContentAlignment VerticalAlignment.Center
                                                        Button.isEnabled (h.Mic <> Mic.Transcribing && h.Pending.IsNone)
                                                        Button.onClick ((fun _ -> dispatch MicPressed), SubPatchOptions.Never)
                                                        Button.content (
                                                            if h.Mic = Mic.Recording then icon Icons.stop Palette.onAccent 18.0 true
                                                            else icon Icons.mic Palette.text 22.0 false)
                                                    ]
                                            ]
                                        ]
                                    Grid.create [
                                        Grid.columnDefinitions "*,Auto"
                                        Grid.children [
                                            Button.create [
                                                Grid.column 0
                                                Button.height 48.0
                                                Button.cornerRadius 24.0
                                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                                Button.horizontalContentAlignment HorizontalAlignment.Center
                                                Button.verticalContentAlignment VerticalAlignment.Center
                                                Button.background Palette.accent
                                                Button.foreground Palette.onAccent
                                                Button.fontSize 16.0
                                                Button.fontWeight FontWeight.SemiBold
                                                Button.content "Continue listening"
                                                Button.onClick ((fun _ -> dispatch (CloseHelp true)), SubPatchOptions.Never)
                                            ]
                                            Border.create [
                                                Grid.column 1
                                                Border.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                                Border.child (pill "Replay" (fun () -> dispatch HelpReplay) false)
                                            ]
                                        ]
                                    ]
                                ]
                            ]
                        )
                    ]
                    // a new question starts a fresh scroll view, at the top where the newest exchange is
                    View.withKey (sprintf "help-%d-%b" h.History.Length h.Pending.IsSome) (
                    ScrollViewer.create [
                        ScrollViewer.content (
                            StackPanel.create [
                                StackPanel.margin (Thickness(20.0, 8.0, 20.0, 20.0))
                                StackPanel.spacing 18.0
                                StackPanel.children [
                                    // what the questions are about
                                    StackPanel.create [
                                        StackPanel.spacing 8.0
                                        StackPanel.children [
                                            sectionLabel "ABOUT"
                                            match about with
                                            | Some v -> visualThumb r v.Id 150.0 dispatch
                                            | None -> ()
                                            TextBlock.create [
                                                TextBlock.text ("“" + seg.Say + "”")
                                                TextBlock.fontSize 14.0
                                                TextBlock.lineHeight 20.0
                                                TextBlock.fontStyle FontStyle.Italic
                                                TextBlock.foreground Palette.muted
                                                TextBlock.textWrapping TextWrapping.Wrap
                                                TextBlock.maxLines 3
                                                TextBlock.textTrimming TextTrimming.WordEllipsis
                                            ]
                                        ]
                                    ]
                                    if not hasKey then
                                        StackPanel.create [
                                            StackPanel.spacing 10.0
                                            StackPanel.children [
                                                label "Asking questions needs a Mistral API key: the answers come from a model that has read the whole paper." 15.0 Palette.text
                                                StackPanel.create [
                                                    StackPanel.orientation Orientation.Horizontal
                                                    StackPanel.children [ pill "Open settings" (fun () -> dispatch (SetShowSettings true)) false ]
                                                ]
                                            ]
                                        ]
                                    match h.Pending with
                                    | Some p ->
                                        StackPanel.create [
                                            StackPanel.spacing 10.0
                                            StackPanel.children [
                                                label p.Question 15.0 Palette.accent
                                                if p.Partial = "" then
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 10.0
                                                        StackPanel.children [
                                                            ProgressBar.create [
                                                                ProgressBar.isIndeterminate true
                                                                ProgressBar.width 48.0
                                                                ProgressBar.minWidth 48.0
                                                                ProgressBar.height 4.0
                                                                ProgressBar.minHeight 4.0
                                                                ProgressBar.verticalAlignment VerticalAlignment.Center
                                                                ProgressBar.foreground Palette.accent
                                                            ]
                                                            label (match h.Preparing with Some step -> step + "…" | None -> "Thinking…") 14.0 Palette.muted
                                                        ]
                                                    ]
                                                else answerBody p.Partial
                                            ]
                                        ]
                                    | None -> ()
                                    for t in session do turnView model r h t false dispatch
                                    if not earlier.IsEmpty then
                                        textLink
                                            (if h.ShowEarlier then "Hide earlier questions" else sprintf "Earlier questions about this paper (%d)" earlier.Length)
                                            (fun () -> dispatch ToggleEarlier) h.ShowEarlier
                                        if h.ShowEarlier then
                                            for t in earlier do turnView model r h t true dispatch
                                ]
                            ]
                        )
                    ])
                ]
            ]
        )
    ]

// ---------------------------------------------------------------------------------------------
// Learn: flashcards, made by the model and scheduled with FSRS
// ---------------------------------------------------------------------------------------------

/// An equation or figure of a paper on a card.
let private cardImage (paperId: string) (visual: string) (maxHeight: float) : IView =
    let paths = Store.Paths((Services.get ()).DataDir)
    match bitmap (paths.Image(paperId, visual)) with
    | Some bmp ->
        Border.create [
            Border.background Palette.paper
            Border.cornerRadius 12.0
            Border.padding (Thickness(10.0, 8.0))
            Border.horizontalAlignment HorizontalAlignment.Left
            Border.child (
                Image.create [
                    Image.source bmp
                    Image.stretch Stretch.Uniform
                    Image.maxWidth (float bmp.PixelSize.Width * 0.6)
                    Image.maxHeight (min maxHeight (float bmp.PixelSize.Height * 0.6))
                    Image.horizontalAlignment HorizontalAlignment.Left
                ]
            )
        ]
    | None -> Border.create [ Border.isVisible false ]

/// When a card comes back: "new", "due now", "in 4 d".
let private dueText (c: Card) =
    let now = DateTime.UtcNow
    if c.Memory.Stage = CardStage.New then "new"
    elif Fsrs.isDue now c.Memory then "due now"
    else "again in " + Fsrs.formatInterval (c.Memory.Due - now)

/// A card in a list: question, answer, and a way to remove it.
let private cardRow (paperId: string) (c: Card) (remove: string) (dispatch: Msg -> unit) : IView =
    Border.create [
        Border.padding (Thickness(16.0, 12.0, 16.0, 6.0))
        Border.cornerRadius 14.0
        Border.background Palette.surface
        Border.child (
            StackPanel.create [
                StackPanel.spacing 6.0
                StackPanel.children [
                    richText c.Front 15.0 Palette.text
                    richText c.Back 14.0 Palette.muted
                    match c.Visual with
                    | Some v -> cardImage paperId v 90.0
                    | None -> ()
                    Grid.create [
                        Grid.columnDefinitions "*,Auto"
                        Grid.children [
                            TextBlock.create [
                                Grid.column 0
                                TextBlock.text (dueText c)
                                TextBlock.fontSize 12.0
                                TextBlock.foreground Palette.faint
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                            Border.create [ Grid.column 1; Border.child (textLink remove (fun () -> dispatch (DeleteCard(paperId, c.Id))) ("remove", c.Id)) ]
                        ]
                    ]
                ]
            ]
        )
    ]

let private spinnerLine (text: string) : IView =
    StackPanel.create [
        StackPanel.orientation Orientation.Horizontal
        StackPanel.spacing 10.0
        StackPanel.children [
            ProgressBar.create [
                ProgressBar.isIndeterminate true
                ProgressBar.width 48.0
                ProgressBar.minWidth 48.0
                ProgressBar.height 4.0
                ProgressBar.minHeight 4.0
                ProgressBar.verticalAlignment VerticalAlignment.Center
                ProgressBar.foreground Palette.accent
            ]
            TextBlock.create [
                TextBlock.text text
                TextBlock.fontSize 14.0
                TextBlock.foreground Palette.muted
                TextBlock.verticalAlignment VerticalAlignment.Center
                TextBlock.textWrapping TextWrapping.Wrap
            ]
        ]
    ]

/// Cards being made for a paper: what, how far, and a way to stop.
let private makingLine (m: MakingCards) (dispatch: Msg -> unit) : IView =
    StackPanel.create [
        StackPanel.spacing 4.0
        StackPanel.children [
            spinnerLine (
                match m.Step with
                | Some step -> step + "…"
                | None when m.Count = 0 -> "Writing cards…"
                | None when m.Count = 1 -> "Writing cards… 1 so far"
                | None -> sprintf "Writing cards… %d so far" m.Count)
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.children [
                    label m.What 13.0 Palette.faint
                    textLink "   Cancel" (fun () -> dispatch CancelMaking) "cancel-making"
                ]
            ]
        ]
    ]

/// "Study this paper" at the top of the reader's Learn panel.
let private studyHero (model: Model) (r: ReaderState) (dispatch: Msg -> unit) : IView =
    let progress = model.Studied.TryFind r.Paper.Id
    Border.create [
        Border.padding (Thickness(18.0, 16.0))
        Border.cornerRadius 18.0
        Border.background Palette.note
        Border.child (
            StackPanel.create [
                StackPanel.spacing 8.0
                StackPanel.children [
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 10.0
                        StackPanel.children [
                            icon Icons.study Palette.accent 24.0 false
                            TextBlock.create [
                                TextBlock.text "Study this paper"
                                TextBlock.fontSize 18.0
                                TextBlock.fontWeight FontWeight.Bold
                                TextBlock.foreground Palette.text
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                        ]
                    ]
                    label
                        (match progress with
                         | Some (d, n) when d >= n -> sprintf "You've studied all %d ideas. They come back for review before you'd forget them." n
                         | Some (d, n) -> sprintf "%d of %d ideas done. Carry on where you left off." d n
                         | None -> "A tutor teaches every idea in it and checks you understood each one, skipping what you already know from other papers.")
                        14.0 Palette.muted
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.children [
                            pill
                                (match progress with
                                 | Some (d, n) when d >= n -> "Open"
                                 | Some _ -> "Continue studying"
                                 | None -> "Start studying")
                                (fun () -> dispatch OpenStudy) true
                        ]
                    ]
                ]
            ]
        )
    ]

/// The Remember panel: cards about where the listener is, typed topics, or the whole paper.
let private cardsOverlay (model: Model) (r: ReaderState) (panel: CardsPanel) (dispatch: Msg -> unit) : IView =
    let about = panel.About |> Option.bind r.Script.Visual
    let seg = r.Script.Segments.[max 0 (min panel.Position (r.Script.Segments.Length - 1))]
    let hasKey = Settings.hasKey model.Settings
    let deck = deckOf model r.Paper.Id
    let due = Cards.dueCount DateTime.UtcNow deck
    let making = model.Making |> Option.filter (fun m -> m.PaperId = r.Paper.Id)
    let busy = model.Making.IsSome
    let justMade = match model.Made with Some (p, cards) when p = r.Paper.Id -> cards | _ -> []
    let justIds = justMade |> List.map (fun c -> c.Id) |> Set.ofList
    let paperDeck = deck |> List.exists (fun c -> c.Origin = "paper")
    let taps =
        [ match about with
          | Some v -> yield Cards.Request.Visual v.Id, sprintf "Cards on %s" (Help.spokenName v)
          | None -> ()
          yield Cards.Request.Moment, "What I just heard"
          yield Cards.Request.Section, "This section's main points" ]
    Border.create [
        Border.background Palette.bg
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "*,Auto"
                        Grid.margin (Thickness(20.0, 12.0, 8.0, 4.0))
                        Grid.children [
                            StackPanel.create [
                                Grid.column 0
                                StackPanel.verticalAlignment VerticalAlignment.Center
                                StackPanel.children [
                                    TextBlock.create [
                                        TextBlock.text "Learn"
                                        TextBlock.fontSize 22.0
                                        TextBlock.fontWeight FontWeight.Bold
                                        TextBlock.foreground Palette.text
                                    ]
                                    label "Study the paper with a tutor, or keep what you hear as flashcards." 12.0 Palette.muted
                                ]
                            ]
                            Border.create [ Grid.column 1; Border.child (iconButton Icons.close 22.0 (fun () -> dispatch (CloseCards false)) "close-cards") ]
                        ]
                    ]
                    Border.create [
                        DockPanel.dock Dock.Bottom
                        Border.background Palette.surface
                        Border.cornerRadius (24.0, 24.0, 0.0, 0.0)
                        Border.padding (Thickness(16.0, 14.0, 16.0, 16.0))
                        Border.child (
                            StackPanel.create [
                                StackPanel.spacing 10.0
                                StackPanel.children [
                                    match model.MakeError with
                                    | Some e -> label e 14.0 Palette.danger
                                    | None -> ()
                                    if hasKey && not busy then
                                        WrapPanel.create [
                                            WrapPanel.children [ for req, text in taps -> chip text (fun () -> dispatch (MakeCards(r.Paper.Id, req))) ]
                                        ]
                                    if hasKey then
                                        Grid.create [
                                            Grid.columnDefinitions "*,Auto"
                                            Grid.children [
                                                TextBox.create [
                                                    Grid.column 0
                                                    TextBox.text panel.Input
                                                    TextBox.watermark "What do you want to remember?"
                                                    TextBox.fontSize 15.0
                                                    TextBox.cornerRadius 20.0
                                                    TextBox.padding (Thickness(14.0, 9.0))
                                                    TextBox.verticalContentAlignment VerticalAlignment.Center
                                                    TextBox.onTextChanged ((fun t -> if t <> panel.Input then dispatch (SetCardInput t)), SubPatchOptions.OnChangeOf panel.Input)
                                                    TextBox.onKeyDown ((fun e -> if e.Key = Input.Key.Enter then e.Handled <- true; dispatch SendCardInput), SubPatchOptions.Never)
                                                ]
                                                if panel.Input.Trim() <> "" then
                                                    Button.create [
                                                        Grid.column 1
                                                        Button.width 44.0
                                                        Button.height 44.0
                                                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                                        Button.cornerRadius 22.0
                                                        Button.padding 0.0
                                                        Button.background Palette.accent
                                                        Button.horizontalContentAlignment HorizontalAlignment.Center
                                                        Button.verticalContentAlignment VerticalAlignment.Center
                                                        Button.isEnabled (not busy)
                                                        Button.onClick ((fun _ -> dispatch SendCardInput), SubPatchOptions.Never)
                                                        Button.content (icon Icons.send Palette.onAccent 20.0 false)
                                                    ]
                                            ]
                                        ]
                                    Grid.create [
                                        Grid.columnDefinitions "*,Auto"
                                        Grid.children [
                                            Button.create [
                                                Grid.column 0
                                                Button.height 48.0
                                                Button.cornerRadius 24.0
                                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                                Button.horizontalContentAlignment HorizontalAlignment.Center
                                                Button.verticalContentAlignment VerticalAlignment.Center
                                                Button.background Palette.accent
                                                Button.foreground Palette.onAccent
                                                Button.fontSize 16.0
                                                Button.fontWeight FontWeight.SemiBold
                                                Button.content "Continue listening"
                                                Button.onClick ((fun _ -> dispatch (CloseCards true)), SubPatchOptions.Never)
                                            ]
                                            if due > 0 then
                                                Border.create [
                                                    Grid.column 1
                                                    Border.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                                    Border.child (pill (sprintf "Review %d" due) (fun () -> dispatch (StartReview(Some r.Paper.Id))) false)
                                                ]
                                        ]
                                    ]
                                ]
                            ]
                        )
                    ]
                    View.withKey (sprintf "cards-%d-%b" deck.Length making.IsSome) (
                    ScrollViewer.create [
                        ScrollViewer.content (
                            StackPanel.create [
                                StackPanel.margin (Thickness(20.0, 8.0, 20.0, 20.0))
                                StackPanel.spacing 14.0
                                StackPanel.children [
                                    studyHero model r dispatch
                                    // what "this" is
                                    StackPanel.create [
                                        StackPanel.spacing 8.0
                                        StackPanel.children [
                                            sectionLabel "REMEMBER WHERE YOU ARE"
                                            match about with
                                            | Some v -> visualThumb r v.Id 150.0 dispatch
                                            | None -> ()
                                            TextBlock.create [
                                                TextBlock.text ("“" + seg.Say + "”")
                                                TextBlock.fontSize 14.0
                                                TextBlock.lineHeight 20.0
                                                TextBlock.fontStyle FontStyle.Italic
                                                TextBlock.foreground Palette.muted
                                                TextBlock.textWrapping TextWrapping.Wrap
                                                TextBlock.maxLines 3
                                                TextBlock.textTrimming TextTrimming.WordEllipsis
                                            ]
                                        ]
                                    ]
                                    if not hasKey then
                                        StackPanel.create [
                                            StackPanel.spacing 10.0
                                            StackPanel.children [
                                                label "Making cards needs a Mistral API key: a model that has read the whole paper writes them." 15.0 Palette.text
                                                StackPanel.create [
                                                    StackPanel.orientation Orientation.Horizontal
                                                    StackPanel.children [ pill "Open settings" (fun () -> dispatch (SetShowSettings true)) false ]
                                                ]
                                            ]
                                        ]
                                    match making, model.Making with
                                    | Some m, _ -> makingLine m dispatch
                                    | None, Some _ -> label "Making cards for another paper…" 14.0 Palette.muted
                                    | None, None -> ()
                                    if not justMade.IsEmpty then
                                        sectionLabel (if justMade.Length = 1 then "JUST MADE" else sprintf "JUST MADE · %d CARDS" justMade.Length)
                                        label "Remove any you don't want to learn." 12.0 Palette.faint
                                        for c in justMade do cardRow r.Paper.Id c "Remove" dispatch
                                    if hasKey then
                                        sectionLabel "THE WHOLE PAPER"
                                        StackPanel.create [
                                            StackPanel.spacing 8.0
                                            StackPanel.children [
                                                label
                                                    (if paperDeck then "Add cards on what the deck doesn't cover yet."
                                                     else "A deck on the problem, the key idea, the method and its equations, and the results. It takes a minute or two.")
                                                    13.0 Palette.muted
                                                StackPanel.create [
                                                    StackPanel.orientation Orientation.Horizontal
                                                    StackPanel.children [
                                                        pill
                                                            (if paperDeck then "Make more cards" else sprintf "Make a deck of about %d cards" (Cards.deckSize r.Script))
                                                            (fun () -> dispatch (MakeCards(r.Paper.Id, Cards.Request.Paper(if paperDeck then 10 else Cards.deckSize r.Script))))
                                                            false
                                                    ]
                                                ]
                                            ]
                                        ]
                                    let rest = deck |> List.filter (fun c -> not (justIds.Contains c.Id)) |> List.rev
                                    if not rest.IsEmpty then
                                        sectionLabel (sprintf "CARDS FROM THIS PAPER · %d" deck.Length)
                                        for c in rest do cardRow r.Paper.Id c "Delete" dispatch
                                ]
                            ]
                        )
                    ])
                ]
            ]
        )
    ]

/// One answer button: the rating, and when the card would come back.
let private rateButton (column: int) (text: string) (after: TimeSpan) (color: string) (background: string) (rating: Fsrs.Rating) (dispatch: Msg -> unit) : IView =
    Button.create [
        Grid.column column
        Button.height 60.0
        Button.margin (Thickness(3.0, 0.0))
        Button.cornerRadius 16.0
        Button.horizontalAlignment HorizontalAlignment.Stretch
        Button.horizontalContentAlignment HorizontalAlignment.Center
        Button.verticalContentAlignment VerticalAlignment.Center
        Button.background background
        Button.onClick ((fun _ -> dispatch (RateCard rating)), SubPatchOptions.OnChangeOf text)
        Button.content (
            StackPanel.create [
                StackPanel.spacing 2.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text text
                        TextBlock.fontSize 15.0
                        TextBlock.fontWeight FontWeight.SemiBold
                        TextBlock.foreground color
                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                    ]
                    TextBlock.create [
                        TextBlock.text (Fsrs.formatInterval after)
                        TextBlock.fontSize 12.0
                        TextBlock.foreground (if background = Palette.accent then Palette.onAccent else Palette.muted)
                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                    ]
                ]
            ]
        )
    ]

/// An option of a multiple-choice question: before the answer, tap to pick it; after, green if right, red if picked
/// and wrong, with why.
let private choiceOption (q: Question) (i: int) (position: int) (choice: int option option) (onPick: unit -> unit) : IView =
    let answered = choice.IsSome
    let picked = choice = Some(Some i)
    let right = i = q.Correct
    let background, border =
        if not answered then Palette.surfaceHigh, Palette.line
        elif right then Palette.goodBg, Palette.good
        elif picked then Palette.badBg, Palette.danger
        else Palette.surface, Palette.surface
    Border.create [
        Border.background background
        Border.borderBrush border
        Border.borderThickness 1.5
        Border.cornerRadius 14.0
        Border.padding (Thickness(12.0, 11.0, 14.0, 11.0))
        Border.cursor (if answered then Input.Cursor.Default else new Input.Cursor(Input.StandardCursorType.Hand))
        Border.onTapped ((fun _ -> if not answered then onPick ()), SubPatchOptions.OnChangeOf(q.Id, i, answered))
        Border.child (
            Grid.create [
                Grid.columnDefinitions "Auto,*"
                Grid.children [
                    Border.create [
                        Grid.column 0
                        Border.width 28.0
                        Border.height 28.0
                        Border.cornerRadius 14.0
                        Border.margin (Thickness(0.0, 0.0, 12.0, 0.0))
                        Border.verticalAlignment VerticalAlignment.Top
                        Border.background (if answered && right then Palette.good elif picked then Palette.danger else Palette.bg)
                        Border.child (
                            TextBlock.create [
                                TextBlock.text (string "ABCDEF".[position])
                                TextBlock.fontSize 14.0
                                TextBlock.fontWeight FontWeight.Bold
                                TextBlock.foreground (if answered && (right || picked) then Palette.onAccent else Palette.muted)
                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                        )
                    ]
                    StackPanel.create [
                        Grid.column 1
                        StackPanel.spacing 6.0
                        StackPanel.verticalAlignment VerticalAlignment.Center
                        StackPanel.children [
                            richParagraph q.Options.[i] 16.0 (if answered && not right && not picked then Palette.muted else Palette.text)
                            if answered && i < q.Why.Length && q.Why.[i] <> "" then
                                richParagraph q.Why.[i] 13.0 (if right || picked then Palette.text else Palette.faint)
                        ]
                    ]
                ]
            ]
        )
    ]

/// "I don't know": better than a guess, which would make an idea look known when it isn't.
let private dontKnow (onPick: unit -> unit) : IView =
    plainButton "Transparent" [
        Button.horizontalAlignment HorizontalAlignment.Stretch
        Button.horizontalContentAlignment HorizontalAlignment.Center
        Button.padding (Thickness(12.0, 12.0))
        Button.cornerRadius 14.0
        Button.borderBrush Palette.line
        Button.borderThickness 1.0
        Button.foreground Palette.muted
        Button.fontSize 15.0
        Button.content "I don't know"
        Button.onClick ((fun _ -> onPick ()), SubPatchOptions.Never)
    ]

/// A wide button at the bottom of a panel.
let private wideButton (column: int) (text: string) (primary: bool) (enabled: bool) (onClick: unit -> unit) : IView =
    Button.create [
        Grid.column column
        Button.height 54.0
        Button.margin (Thickness(3.0, 0.0))
        Button.cornerRadius 18.0
        Button.horizontalAlignment HorizontalAlignment.Stretch
        Button.horizontalContentAlignment HorizontalAlignment.Center
        Button.verticalContentAlignment VerticalAlignment.Center
        Button.background (if primary then Palette.accent else Palette.surfaceHigh)
        Button.foreground (if primary then Palette.onAccent else Palette.text)
        Button.fontSize 16.0
        Button.fontWeight FontWeight.SemiBold
        Button.isEnabled enabled
        Button.content text
        Button.onClick ((fun _ -> onClick ()), SubPatchOptions.OnChangeOf text)
    ]

/// A review session: the question, then the answer and how well it was remembered. Flashcards are rated; an idea's
/// multiple-choice question is answered by picking, its recall question is rated like a card.
let private reviewOverlay (model: Model) (rv: ReviewState) (dispatch: Msg -> unit) : IView =
    let left = rv.Queue.Length
    let paperTitle (id: string) = model.Papers |> List.tryFind (fun p -> p.Id = id) |> Option.map (fun p -> p.Title)
    Border.create [
        Border.background Palette.bg
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "Auto,*"
                        Grid.margin (Thickness(4.0, 6.0, 20.0, 0.0))
                        Grid.children [
                            Border.create [ Grid.column 0; Border.child (iconButton Icons.close 22.0 (fun () -> dispatch CloseReview) "close-review") ]
                            TextBlock.create [
                                Grid.column 1
                                TextBlock.text (if left = 0 then "Review" else sprintf "Review · %d left" left)
                                TextBlock.fontSize 18.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground Palette.text
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                        ]
                    ]
                    ProgressBar.create [
                        DockPanel.dock Dock.Top
                        ProgressBar.margin (Thickness(20.0, 4.0, 20.0, 8.0))
                        ProgressBar.minimum 0.0
                        ProgressBar.maximum 1.0
                        ProgressBar.value (float rv.Answered / float (max 1 (rv.Answered + left)))
                        ProgressBar.height 4.0
                        ProgressBar.minHeight 4.0
                        ProgressBar.cornerRadius 2.0
                        ProgressBar.foreground Palette.accent
                        ProgressBar.background Palette.surfaceHigh
                    ]
                    match rv.Queue with
                    | [] ->
                        let now = DateTime.UtcNow
                        let inScope (id: string) = rv.Scope.IsNone || rv.Scope = Some id
                        let next =
                            Seq.append
                                (model.Decks |> Map.toSeq |> Seq.filter (fst >> inScope) |> Seq.collect snd |> Seq.map (fun c -> c.Memory.Due))
                                (model.Concepts
                                 |> Map.toSeq
                                 |> Seq.map snd
                                 |> Seq.filter (fun c -> c.Sources |> List.exists (fun x -> inScope x.PaperId))
                                 |> Seq.map (fun c -> c.Memory.Due))
                            |> Seq.sort
                            |> Seq.tryHead
                        StackPanel.create [
                            StackPanel.margin (Thickness(32.0, 0.0))
                            StackPanel.verticalAlignment VerticalAlignment.Center
                            StackPanel.spacing 12.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text "Done for now"
                                    TextBlock.fontSize 26.0
                                    TextBlock.fontWeight FontWeight.Bold
                                    TextBlock.foreground Palette.text
                                ]
                                label
                                    (sprintf "%d answer%s, %d remembered." rv.Answered (if rv.Answered = 1 then "" else "s") rv.Remembered)
                                    16.0 Palette.muted
                                match next with
                                | Some d when d > now -> label (sprintf "The next one comes back in %s." (Fsrs.formatInterval (d - now))) 14.0 Palette.faint
                                | _ -> ()
                                StackPanel.create [
                                    StackPanel.orientation Orientation.Horizontal
                                    StackPanel.children [ pill "Close" (fun () -> dispatch CloseReview) true ]
                                ]
                            ]
                        ]
                    | item :: _ ->
                        let now = DateTime.UtcNow
                        let key, title, memory =
                            match item with
                            | Knowledge.ReviewItem.Card (paperId, c) -> c.Id, defaultArg (paperTitle paperId) "", c.Memory
                            | Knowledge.ReviewItem.Concept (c, q) ->
                                q.Id,
                                (match c.Sources with
                                 | s :: _ -> "An idea from " + (paperTitle s.PaperId |> Option.defaultValue s.Title)
                                 | [] -> "An idea"),
                                c.Memory
                        let preview = Fsrs.preview model.Settings.Retention now key memory |> Map.ofList
                        let listen =
                            match item with
                            | Knowledge.ReviewItem.Card (paperId, c) -> Some(paperId, c.Segment)
                            | Knowledge.ReviewItem.Concept (c, _) ->
                                Knowledge.whereToListen (fun id -> model.Papers |> List.exists (fun p -> p.Id = id)) c
                                |> Option.map (fun s -> s.PaperId, s.Segment)
                        let listenLink =
                            match listen with
                            | Some (paperId, segment) -> textLink "Listen to it in the paper" (fun () -> dispatch (ListenToCard(paperId, segment))) ("listen", key)
                            | None -> Border.create [ Border.isVisible false ]
                        let rateRow () =
                            Grid.create [
                                Grid.columnDefinitions "*,*,*,*"
                                Grid.children [
                                    rateButton 0 "Again" preview.[Fsrs.Rating.Again] Palette.danger Palette.surfaceHigh Fsrs.Rating.Again dispatch
                                    rateButton 1 "Hard" preview.[Fsrs.Rating.Hard] Palette.text Palette.surfaceHigh Fsrs.Rating.Hard dispatch
                                    rateButton 2 "Good" preview.[Fsrs.Rating.Good] Palette.onAccent Palette.accent Fsrs.Rating.Good dispatch
                                    rateButton 3 "Easy" preview.[Fsrs.Rating.Easy] Palette.text Palette.surfaceHigh Fsrs.Rating.Easy dispatch
                                ]
                            ]
                            :> IView
                        let showButton () = Grid.create [ Grid.children [ wideButton 0 "Show answer" true true (fun () -> dispatch ShowAnswer) ] ] :> IView
                        let body, footer =
                            match item with
                            | Knowledge.ReviewItem.Card (paperId, c) ->
                                [ richText c.Front 22.0 Palette.text
                                  match c.Visual with
                                  | Some v when c.VisualOnFront -> cardImage paperId v 260.0
                                  | _ -> ()
                                  if rv.Revealed then
                                      Border.create [ Border.height 1.0; Border.background Palette.line; Border.margin (Thickness(0.0, 6.0)) ]
                                      richText c.Back 19.0 Palette.text
                                      match c.Visual with
                                      | Some v when not c.VisualOnFront -> cardImage paperId v 260.0
                                      | _ -> ()
                                      StackPanel.create [
                                          StackPanel.orientation Orientation.Horizontal
                                          StackPanel.children [
                                              listenLink
                                              textLink "Delete card" (fun () -> dispatch (DeleteCard(paperId, c.Id))) ("delete", c.Id)
                                          ]
                                      ] ],
                                Some(if rv.Revealed then rateRow () else showButton ())
                            | Knowledge.ReviewItem.Concept (c, q) when q.Options.IsEmpty ->
                                [ label c.Name 14.0 Palette.accent
                                  richText q.Prompt 22.0 Palette.text
                                  match q.Visual with
                                  | Some v -> cardImage q.PaperId v 260.0
                                  | None -> ()
                                  label "Answer it in your head, or out loud, before you look." 13.0 Palette.faint
                                  if rv.Revealed then
                                      Border.create [ Border.height 1.0; Border.background Palette.line; Border.margin (Thickness(0.0, 6.0)) ]
                                      richText q.Answer 19.0 Palette.text
                                      listenLink ],
                                Some(if rv.Revealed then rateRow () else showButton ())
                            | Knowledge.ReviewItem.Concept (c, q) ->
                                let right = rv.Choice = Some(Some q.Correct)
                                [ label c.Name 14.0 Palette.accent
                                  richText q.Prompt 22.0 Palette.text
                                  match q.Visual with
                                  | Some v -> cardImage q.PaperId v 260.0
                                  | None -> ()
                                  StackPanel.create [
                                      StackPanel.spacing 8.0
                                      StackPanel.children [
                                          let order = if rv.Order.Length = q.Options.Length then rv.Order else [ 0 .. q.Options.Length - 1 ]
                                          for pos, i in List.indexed order do
                                              choiceOption q i pos rv.Choice (fun () -> dispatch (ReviewChoose(Some i)))
                                          if rv.Choice.IsNone then dontKnow (fun () -> dispatch (ReviewChoose None))
                                      ]
                                  ]
                                  match rv.Choice, model.Concepts.TryFind c.Id with
                                  | Some _, Some now' ->
                                      label
                                          (sprintf "%s It comes back in %s." (if right then "Right." else "Now you know it.")
                                               (Fsrs.formatInterval (now'.Memory.Due - now)))
                                          14.0 (if right then Palette.good else Palette.muted)
                                      listenLink
                                  | _ -> () ],
                                (if rv.Choice.IsSome then Some(Grid.create [ Grid.children [ wideButton 0 "Continue" true true (fun () -> dispatch ReviewNext) ] ] :> IView)
                                 else None)
                        Grid.create [
                            Grid.rowDefinitions "*,Auto"
                            Grid.children [
                                View.withKey (sprintf "review-%s-%b-%A" key rv.Revealed rv.Choice) (
                                ScrollViewer.create [
                                    Grid.row 0
                                    ScrollViewer.content (
                                        StackPanel.create [
                                            StackPanel.margin (Thickness(24.0, 16.0, 24.0, 16.0))
                                            StackPanel.spacing 16.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text title
                                                    TextBlock.fontSize 12.0
                                                    TextBlock.foreground Palette.faint
                                                    TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                ]
                                                yield! body
                                            ]
                                        ]
                                    )
                                ])
                                match footer with
                                | Some f ->
                                    Border.create [
                                        Grid.row 1
                                        Border.background Palette.surface
                                        Border.cornerRadius (24.0, 24.0, 0.0, 0.0)
                                        Border.padding (Thickness(13.0, 16.0, 13.0, 18.0))
                                        Border.child f
                                    ]
                                | None -> ()
                            ]
                        ]
                ]
            ]
        )
    ]

/// The Learn screen: cards due across the library, and each paper's deck.
/// An idea the learner knows, in the list on the Learn screen.
let private knownRow (model: Model) (c: Concept) (dispatch: Msg -> unit) : IView =
    let now = DateTime.UtcNow
    let from =
        match c.Sources with
        | [] -> ""
        | s :: rest -> sprintf " · from %s%s" s.Title (if rest.IsEmpty then "" else sprintf " and %d more" rest.Length)
    let next = if Fsrs.isDue now c.Memory then "review due" else "review in " + Fsrs.formatInterval (c.Memory.Due - now)
    Border.create [
        Border.padding (Thickness(16.0, 12.0, 16.0, 6.0))
        Border.cornerRadius 14.0
        Border.background Palette.surface
        Border.child (
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.children [
                    richText c.Name 15.0 Palette.text
                    richText c.Definition 13.0 Palette.muted
                    Grid.create [
                        Grid.columnDefinitions "*,Auto"
                        Grid.children [
                            TextBlock.create [
                                Grid.column 0
                                TextBlock.text (sprintf "%.0f%% remembered now · %s%s" (100.0 * Knowledge.strength now c) next from)
                                TextBlock.fontSize 12.0
                                TextBlock.foreground Palette.faint
                                TextBlock.textWrapping TextWrapping.Wrap
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                            Border.create [ Grid.column 1; Border.child (textLink "Forget" (fun () -> dispatch (ForgetConcept c.Id)) ("forget", c.Id)) ]
                        ]
                    ]
                ]
            ]
        )
    ]

let private learnView (model: Model) (dispatch: Msg -> unit) : IView =
    let now = DateTime.UtcNow
    let due = reviewQueue model None now |> List.length
    let all = model.Decks |> Map.toList |> List.collect snd
    let concepts = model.Concepts |> Map.toList |> List.map snd |> List.filter (fun c -> c.Memory.Stage <> CardStage.New)
    let learned = (all |> List.filter (fun c -> c.Memory.Stage = CardStage.Review) |> List.length) + concepts.Length
    let hasKey = Settings.hasKey model.Settings
    DockPanel.create [
        DockPanel.children [
            Grid.create [
                DockPanel.dock Dock.Top
                Grid.columnDefinitions "Auto,*"
                Grid.margin (Thickness(4.0, 6.0, 16.0, 0.0))
                Grid.children [
                    Border.create [ Grid.column 0; Border.child (iconButton Icons.chevronLeft 24.0 (fun () -> dispatch CloseLearn) "close-learn") ]
                    TextBlock.create [
                        Grid.column 1
                        TextBlock.text "Learn"
                        TextBlock.fontSize 24.0
                        TextBlock.fontWeight FontWeight.Bold
                        TextBlock.foreground Palette.text
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                ]
            ]
            ScrollViewer.create [
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.margin (Thickness(16.0, 8.0, 16.0, 24.0))
                        StackPanel.spacing 10.0
                        StackPanel.children [
                            Border.create [
                                Border.padding (Thickness(20.0, 18.0))
                                Border.cornerRadius 18.0
                                Border.background "#1A2436"
                                Border.child (
                                    StackPanel.create [
                                        StackPanel.spacing 8.0
                                        StackPanel.children [
                                            TextBlock.create [
                                                TextBlock.text (
                                                    if all.IsEmpty && concepts.IsEmpty then "Nothing to review yet"
                                                    elif due = 0 then "All caught up"
                                                    elif due = 1 then "1 thing to review"
                                                    else sprintf "%d things to review" due)
                                                TextBlock.fontSize 22.0
                                                TextBlock.fontWeight FontWeight.Bold
                                                TextBlock.foreground Palette.text
                                            ]
                                            label
                                                (if all.IsEmpty && concepts.IsEmpty then
                                                     "Study a paper below: a tutor teaches it idea by idea and checks you understood. Or make flashcards: from any paper below, or while listening with the cards button in the reader."
                                                 else
                                                     let next = (all |> List.map (fun c -> c.Memory.Due)) @ (concepts |> List.map (fun c -> c.Memory.Due)) |> List.filter (fun d -> d > now) |> List.sort |> List.tryHead
                                                     let counts =
                                                         [ if not concepts.IsEmpty then sprintf "%d idea%s studied" concepts.Length (if concepts.Length = 1 then "" else "s")
                                                           if not all.IsEmpty then sprintf "%d card%s" all.Length (if all.Length = 1 then "" else "s") ]
                                                         |> String.concat ", "
                                                     match next with
                                                     | Some d when due = 0 -> sprintf "%s. The next one comes back in %s." counts (Fsrs.formatInterval (d - now))
                                                     | _ -> counts + ".")
                                                14.0 Palette.muted
                                            if due > 0 then
                                                StackPanel.create [
                                                    StackPanel.orientation Orientation.Horizontal
                                                    StackPanel.margin (Thickness(0.0, 4.0, 0.0, 0.0))
                                                    StackPanel.children [ pill "Review now" (fun () -> dispatch (StartReview None)) true ]
                                                ]
                                        ]
                                    ]
                                )
                            ]
                            match model.MakeError with
                            | Some e -> label e 14.0 Palette.danger
                            | None -> ()
                            if not hasKey then label "Studying and making cards need a Mistral API key (Settings). Reviewing works without one." 13.0 Palette.muted
                            if not model.Papers.IsEmpty then sectionLabel "YOUR PAPERS"
                            for p in model.Papers do
                                let deck = deckOf model p.Id
                                let paperDue = Cards.dueCount now deck
                                // cards and ideas
                                let reviewDue = reviewQueue model (Some p.Id) now |> List.length
                                let making = model.Making |> Option.filter (fun m -> m.PaperId = p.Id)
                                let made = match model.Made with Some (id, cards) when id = p.Id -> cards.Length | _ -> 0
                                let expanded = model.LearnOpen = Some p.Id
                                Border.create [
                                    Border.padding (Thickness(18.0, 14.0, 18.0, 8.0))
                                    Border.cornerRadius 16.0
                                    Border.background Palette.surface
                                    Border.child (
                                        StackPanel.create [
                                            StackPanel.spacing 6.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text p.Title
                                                    TextBlock.fontSize 16.0
                                                    TextBlock.fontWeight FontWeight.SemiBold
                                                    TextBlock.foreground Palette.text
                                                    TextBlock.textWrapping TextWrapping.Wrap
                                                    TextBlock.maxLines 2
                                                    TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                ]
                                                label
                                                    ([ match model.Studied.TryFind p.Id with
                                                       | Some (d, n) when d >= n -> sprintf "studied all %d ideas" n
                                                       | Some (d, n) -> sprintf "studied %d of %d ideas" d n
                                                       | None -> ()
                                                       match deck.Length, paperDue with
                                                       | 0, _ -> ()
                                                       | n, 0 -> sprintf "%d card%s · nothing due" n (if n = 1 then "" else "s")
                                                       | n, d -> sprintf "%d card%s · %d due" n (if n = 1 then "" else "s") d ]
                                                     |> function
                                                         | [] -> "Not studied yet"
                                                         | xs -> String.Join(" · ", xs)
                                                     |> capitalize)
                                                    13.0 Palette.muted
                                                match making with
                                                | Some m -> makingLine m dispatch
                                                | None ->
                                                    if made > 0 then label (sprintf "Added %d card%s." made (if made = 1 then "" else "s")) 13.0 Palette.accent
                                                    WrapPanel.create [
                                                        WrapPanel.children [
                                                            textLink
                                                                (match model.Studied.TryFind p.Id with
                                                                 | Some (d, n) when d >= n -> "Study again"
                                                                 | Some (d, _) when d > 0 -> "Continue studying"
                                                                 | _ -> "Study")
                                                                (fun () -> dispatch (StudyPaper p)) ("study", p.Id)
                                                            if reviewDue > 0 then textLink (sprintf "Review %d" reviewDue) (fun () -> dispatch (StartReview(Some p.Id))) ("review", p.Id)
                                                            if hasKey then
                                                                textLink
                                                                    (if deck |> List.exists (fun c -> c.Origin = "paper") then "Make more cards" else "Make a deck")
                                                                    (fun () ->
                                                                        let size = if deck |> List.exists (fun c -> c.Origin = "paper") then 10 else max 12 (min 30 p.PageCount)
                                                                        dispatch (MakeCards(p.Id, Cards.Request.Paper size)))
                                                                    ("make", p.Id)
                                                            if not deck.IsEmpty then
                                                                textLink (if expanded then "Hide cards" else "Show cards") (fun () -> dispatch (ToggleDeck p.Id)) ("show", p.Id, expanded)
                                                        ]
                                                    ]
                                                if expanded then
                                                    StackPanel.create [
                                                        StackPanel.spacing 8.0
                                                        StackPanel.margin (Thickness(-10.0, 0.0, -10.0, 10.0))
                                                        StackPanel.children [ for c in deck -> cardRow p.Id c "Delete" dispatch ]
                                                    ]
                                            ]
                                        ]
                                    )
                                ]
                            if not concepts.IsEmpty then
                                Grid.create [
                                    Grid.columnDefinitions "*,Auto"
                                    Grid.children [
                                        Border.create [ Grid.column 0; Border.verticalAlignment VerticalAlignment.Center; Border.child (sectionLabel (sprintf "WHAT YOU KNOW · %d IDEA%s" concepts.Length (if concepts.Length = 1 then "" else "S"))) ]
                                        Border.create [ Grid.column 1; Border.child (textLink (if model.ShowKnown then "Hide" else "Show") (fun () -> dispatch ToggleKnown) ("known", model.ShowKnown)) ]
                                    ]
                                ]
                                if model.ShowKnown then
                                    label "Ideas you have studied, across papers. A paper you study next skips the ones you still know well, and checks the fading ones with one question. Forget one to have it taught again." 12.0 Palette.faint
                                    for c in concepts |> List.sortBy (fun c -> c.Name.ToLowerInvariant()) do knownRow model c dispatch
                            label "Reviews are scheduled with FSRS: each card and idea comes back just before you would forget it, so a few minutes a day keeps a paper for months. Ideas come back with a different question each time. For cards, answer honestly: Again if you forgot, Hard if it took real effort, Good if you knew it, Easy if it was instant."
                                12.0 Palette.faint
                            |> fun l -> Border.create [ Border.margin (Thickness(4.0, 12.0, 4.0, 0.0)); Border.child l ]
                        ]
                    ]
                )
            ]
        ]
    ]

// ---------------------------------------------------------------------------------------------
// Study: a tutor teaches the paper idea by idea, checks each one, and skips what the learner knows
// ---------------------------------------------------------------------------------------------

/// Where a plan's idea comes from, as a small heading.
let private ideaSource (script: Script) (idea: Study.Idea) =
    if idea.Background then "BACKGROUND · NOT EXPLAINED IN THE PAPER"
    elif idea.Section > 0 && idea.Section < script.Sections.Length then "FROM THE PAPER · " + script.Sections.[idea.Section].Title.ToUpperInvariant()
    else "FROM THE PAPER"

/// How an idea of the plan stands: done, known, to be checked, or to learn.
let private ideaStatus (model: Model) (s: StudyState) (idea: Study.Idea) : string * bool =
    let now = DateTime.UtcNow
    let concept = s.Progress.Links.TryFind idea.Id |> Option.bind model.Concepts.TryFind
    let strength = concept |> Option.map (fun c -> sprintf " · %.0f%%" (100.0 * Knowledge.strength now c)) |> Option.defaultValue ""
    match s.Progress.Done.TryFind idea.Id with
    | Some Study.Outcome.Learned -> "learned" + strength, true
    | Some Study.Outcome.TestedOut -> "knew it" + strength, true
    | Some Study.Outcome.Refreshed -> "still known" + strength, true
    | Some Study.Outcome.Known -> "you know it", true
    | None when s.Progress.Teach.Contains idea.Id -> (if idea.Background then "background" else ""), false
    | None ->
        match concept with
        | Some c when Knowledge.skippable now c -> "you know it", false
        | Some c when c.Memory.Stage <> CardStage.New -> "quick check", false
        | _ -> (if idea.Background then "background" else ""), false

/// The idea the session goes to next, for marking it on the plan.
let private nextIdea (model: Model) (s: StudyState) =
    match s.Plan with
    | Some plan ->
        match fst (Study.next DateTime.UtcNow plan s.Progress model.Concepts s.Last) with
        | Study.Step.Teach i
        | Study.Step.Check (i, _) -> Some i
        | _ -> None
    | None -> None

let private ideaRow (model: Model) (s: StudyState) (idea: Study.Idea) (current: bool) (dispatch: Msg -> unit) : IView =
    let status, isDone = ideaStatus model s idea
    plainButton (if current then Palette.surfaceHigh else "Transparent") [
        Button.horizontalAlignment HorizontalAlignment.Stretch
        Button.horizontalContentAlignment HorizontalAlignment.Stretch
        Button.padding (Thickness(10.0, 9.0, 12.0, 9.0))
        Button.cornerRadius 12.0
        Button.onClick ((fun _ -> dispatch (StudyTeach idea.Id)), SubPatchOptions.OnChangeOf idea.Id)
        Button.content (
            Grid.create [
                Grid.columnDefinitions "Auto,*,Auto"
                Grid.children [
                    Border.create [
                        Grid.column 0
                        Border.width 22.0
                        Border.margin (Thickness(0.0, 1.0, 10.0, 0.0))
                        Border.verticalAlignment VerticalAlignment.Top
                        Border.child (
                            if isDone then icon Icons.check Palette.accent 18.0 false
                            else
                                Ellipse.create [
                                    Ellipse.width 10.0
                                    Ellipse.height 10.0
                                    Ellipse.margin (Thickness(4.0, 5.0, 0.0, 0.0))
                                    if current then Ellipse.fill Palette.accent
                                    else
                                        Ellipse.stroke Palette.faint
                                        Ellipse.strokeThickness 1.5
                                ]
                        )
                    ]
                    Border.create [ Grid.column 1; Border.child (richParagraph idea.Name 15.0 (if isDone then Palette.muted else Palette.text)) ]
                    TextBlock.create [
                        Grid.column 2
                        TextBlock.text status
                        TextBlock.fontSize 12.0
                        TextBlock.foreground Palette.faint
                        TextBlock.margin (Thickness(10.0, 2.0, 0.0, 0.0))
                        TextBlock.verticalAlignment VerticalAlignment.Top
                    ]
                ]
            ]
        )
    ]

/// The plan: what the paper is about, the ideas part by part and how each stands; or, before there is one, what
/// studying does.
let private studyOverview (model: Model) (r: ReaderState) (s: StudyState) (dispatch: Msg -> unit) : IView list =
    match s.Plan, s.Planning with
    | None, Some (step, names) ->
        [ TextBlock.create [ TextBlock.text "Planning your study"; TextBlock.fontSize 24.0; TextBlock.fontWeight FontWeight.Bold; TextBlock.foreground Palette.text ]
          spinnerLine (step + "…")
          label "The tutor reads the whole paper and breaks it into ideas, in the order to learn them." 13.0 Palette.faint
          StackPanel.create [
              StackPanel.spacing 6.0
              StackPanel.children [ for i, n in List.indexed names -> richParagraph (sprintf "%d. %s" (i + 1) n) 15.0 Palette.text ]
          ] ]
    | None, None ->
        [ TextBlock.create [ TextBlock.text "Study this paper"; TextBlock.fontSize 26.0; TextBlock.fontWeight FontWeight.Bold; TextBlock.foreground Palette.text ]
          label "A tutor takes you through every idea in it, one at a time." 17.0 Palette.text
          StackPanel.create [
              StackPanel.spacing 8.0
              StackPanel.children [
                  for line in
                      [ "A short explanation of each idea, with its equation or figure and an example."
                        "A question to check you understood, with why each answer is right or wrong. Miss it, and the tutor explains what you missed before another one."
                        "At the end of each part, you explain it back in your own words and get feedback."
                        "Ideas you know from papers you studied before are skipped, or checked with one quick question."
                        "What you learn comes back for review just before you'd forget it, a little later each time." ] ->
                      Grid.create [
                          Grid.columnDefinitions "Auto,*"
                          Grid.children [
                              Border.create [ Grid.column 0; Border.margin (Thickness(0.0, 2.0, 10.0, 0.0)); Border.verticalAlignment VerticalAlignment.Top; Border.child (icon Icons.check Palette.accent 16.0 false) ]
                              Border.create [ Grid.column 1; Border.child (label line 15.0 Palette.muted) ]
                          ]
                      ]
              ]
          ]
          label "Stop whenever you like: you carry on where you left off. Planning takes about half a minute." 13.0 Palette.faint
          if not (Settings.hasKey model.Settings) then
              StackPanel.create [
                  StackPanel.spacing 10.0
                  StackPanel.children [
                      label "Studying needs a Mistral API key: the tutor is a model (GLM 5.3) that reads the whole paper." 15.0 Palette.text
                      StackPanel.create [ StackPanel.orientation Orientation.Horizontal; StackPanel.children [ pill "Open settings" (fun () -> dispatch (SetShowSettings true)) false ] ]
                  ]
              ] ]
    | Some plan, _ ->
        let now = DateTime.UtcNow
        let current = nextIdea model s
        let known = plan.Ideas |> List.filter (fun i -> fst (ideaStatus model s i) |> fun t -> t = "you know it" || t.StartsWith "still known") |> List.length
        let minutes = Study.minutesLeft now plan s.Progress model.Concepts
        let uses =
            plan.Ideas |> List.collect (fun i -> i.Uses) |> List.distinct |> List.choose model.Concepts.TryFind |> List.map (fun c -> c.Name)
        [ TextBlock.create [
              TextBlock.text r.Script.Title
              TextBlock.fontSize 22.0
              TextBlock.fontWeight FontWeight.Bold
              TextBlock.foreground Palette.text
              TextBlock.textWrapping TextWrapping.Wrap
          ]
          if plan.Overview <> "" then richText plan.Overview 15.0 Palette.muted
          label
              ([ sprintf "%d ideas" plan.Ideas.Length
                 if s.Progress.Done.Count > 0 then sprintf "%d done" (min plan.Ideas.Length s.Progress.Done.Count)
                 if known > 0 then sprintf "%d you know already" known
                 if minutes > 0 then sprintf "about %d min left" (max 5 (int (Math.Round(float minutes / 5.0)) * 5)) ]
               |> String.concat " · ")
              14.0 Palette.accent
          if not uses.IsEmpty then label ("Builds on what you know: " + String.Join(", ", uses) + ". Not taught again.") 13.0 Palette.faint
          for pi, part in List.indexed plan.Parts do
              StackPanel.create [
                  StackPanel.spacing 2.0
                  StackPanel.children [
                      sectionLabel (sprintf "PART %d · %s" (pi + 1) (part.Title.ToUpperInvariant()))
                      for idea in part.Ideas do ideaRow model s idea (current = Some idea.Id) dispatch
                      if part.Recap <> "" then
                          Grid.create [
                              Grid.columnDefinitions "Auto,*"
                              Grid.margin (Thickness(10.0, 6.0, 12.0, 6.0))
                              Grid.children [
                                  Border.create [
                                      Grid.column 0
                                      Border.width 22.0
                                      Border.margin (Thickness(0.0, 0.0, 10.0, 0.0))
                                      Border.child (if s.Progress.Recaps.Contains pi then icon Icons.check Palette.accent 18.0 false else icon Icons.ask Palette.faint 16.0 false)
                                  ]
                                  TextBlock.create [
                                      Grid.column 1
                                      TextBlock.text "Explain it back, in your own words"
                                      TextBlock.fontSize 14.0
                                      TextBlock.fontStyle FontStyle.Italic
                                      TextBlock.foreground Palette.faint
                                      TextBlock.verticalAlignment VerticalAlignment.Center
                                  ]
                              ]
                          ]
                  ]
              ]
          label "Tap an idea to study it now, even one you know or have done." 12.0 Palette.faint ]

/// The conversation with the tutor about the idea on screen, and ways to ask.
let private tutorSection (model: Model) (s: StudyState) (answeredWrong: bool) (dispatch: Msg -> unit) : IView list =
    let hasKey = Settings.hasKey model.Settings
    let busy = s.Pending.IsSome || s.Mic = Mic.Transcribing
    let taps = if s.Chat.IsEmpty || s.Followups.IsEmpty then Study.tutorTaps answeredWrong else s.Followups
    [ sectionLabel "ASK THE TUTOR"
      for t in s.Chat do
          StackPanel.create [
              StackPanel.spacing 8.0
              StackPanel.children [ label t.Question 15.0 Palette.accent; richText t.Answer 16.0 Palette.text ]
          ]
      match s.Pending with
      | Some (_, q, partial) ->
          StackPanel.create [
              StackPanel.spacing 8.0
              StackPanel.children [ label q 15.0 Palette.accent; (if partial = "" then spinnerLine "Thinking…" else richText partial 16.0 Palette.text) ]
          ]
      | None -> ()
      if hasKey && not busy then
          WrapPanel.create [ WrapPanel.children [ for t in taps -> chip (t.Replace("$", "")) (fun () -> dispatch (StudyAsk(t, true))) ] ]
      if hasKey then
          Grid.create [
              Grid.columnDefinitions "*,Auto,Auto"
              Grid.children [
                  match s.Mic with
                  | Mic.Recording -> label "Listening… tap the button when you're done." 15.0 Palette.text
                  | Mic.Transcribing -> label "Writing down what you said…" 15.0 Palette.muted
                  | Mic.Idle ->
                      TextBox.create [
                          Grid.column 0
                          TextBox.text s.Input
                          TextBox.watermark "Ask anything about it"
                          TextBox.fontSize 15.0
                          TextBox.cornerRadius 20.0
                          TextBox.padding (Thickness(14.0, 9.0))
                          TextBox.verticalContentAlignment VerticalAlignment.Center
                          TextBox.onTextChanged ((fun t -> if t <> s.Input then dispatch (SetStudyInput t)), SubPatchOptions.OnChangeOf s.Input)
                          TextBox.onKeyDown ((fun e -> if e.Key = Input.Key.Enter then e.Handled <- true; dispatch SendStudyInput), SubPatchOptions.Never)
                      ]
                  if s.Mic = Mic.Idle && s.Input.Trim() <> "" then
                      Button.create [
                          Grid.column 1
                          Button.width 44.0
                          Button.height 44.0
                          Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                          Button.cornerRadius 22.0
                          Button.padding 0.0
                          Button.background Palette.accent
                          Button.horizontalContentAlignment HorizontalAlignment.Center
                          Button.verticalContentAlignment VerticalAlignment.Center
                          Button.isEnabled (not busy)
                          Button.onClick ((fun _ -> dispatch SendStudyInput), SubPatchOptions.Never)
                          Button.content (icon Icons.send Palette.onAccent 20.0 false)
                      ]
                  if (Services.get ()).Recorder.IsSome then
                      Button.create [
                          Grid.column 2
                          Button.width 44.0
                          Button.height 44.0
                          Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                          Button.cornerRadius 22.0
                          Button.padding 0.0
                          Button.background (if s.Mic = Mic.Recording then Palette.danger else Palette.surfaceHigh)
                          Button.horizontalContentAlignment HorizontalAlignment.Center
                          Button.verticalContentAlignment VerticalAlignment.Center
                          Button.isEnabled (s.Mic <> Mic.Transcribing && s.Pending.IsNone)
                          Button.onClick ((fun _ -> dispatch StudyMic), SubPatchOptions.Never)
                          Button.content (if s.Mic = Mic.Recording then icon Icons.stop Palette.onAccent 18.0 true else icon Icons.mic Palette.text 22.0 false)
                      ]
              ]
          ] ]

/// An idea's lesson: where it comes from, the explanation beside its equation or figure, an example, and the tutor.
let private studyTeach (model: Model) (r: ReaderState) (s: StudyState) (idea: Study.Idea) (dispatch: Msg -> unit) : IView list =
    let lesson =
        s.Lessons.TryFind idea.Id
        |> Option.orElse (s.Writing.TryFind idea.Id |> Option.map (Study.parseLesson r.Script r.Paper.Id idea.Id))
    let writing = s.Writing.ContainsKey idea.Id
    let ready = s.Lessons.ContainsKey idea.Id
    let isDone = s.Progress.Done.ContainsKey idea.Id
    let uses = idea.Uses |> List.choose model.Concepts.TryFind |> List.map (fun c -> c.Name)
    [ TextBlock.create [
          TextBlock.text (ideaSource r.Script idea)
          TextBlock.fontSize 12.0
          TextBlock.fontWeight FontWeight.Bold
          TextBlock.foreground Palette.accent
          TextBlock.textTrimming TextTrimming.CharacterEllipsis
      ]
      richParagraph idea.Name 24.0 Palette.text |> fun v -> Border.create [ Border.child v; Border.margin (Thickness(0.0, -6.0, 0.0, 0.0)) ]
      if not uses.IsEmpty then label ("Builds on what you know: " + String.Join(", ", uses)) 13.0 Palette.faint
      match lesson with
      | Some l when l.Explanation <> "" ->
          match l.Show with
          | Some v -> visualThumb r v 280.0 dispatch
          | None -> ()
          richText l.Explanation 17.0 Palette.text
          if l.Example <> "" then
              Border.create [
                  Border.background Palette.surface
                  Border.cornerRadius 16.0
                  Border.padding (Thickness(16.0, 12.0, 16.0, 14.0))
                  Border.child (StackPanel.create [ StackPanel.spacing 6.0; StackPanel.children [ sectionLabel "EXAMPLE"; richText l.Example 16.0 Palette.text ] ])
              ]
          if writing then spinnerLine "Writing…"
      | _ ->
          match s.Failed.TryFind idea.Id with
          | Some e ->
              label ("The lesson couldn't be written: " + e) 14.0 Palette.danger
              StackPanel.create [ StackPanel.orientation Orientation.Horizontal; StackPanel.children [ pill "Try again" (fun () -> dispatch (StudyRetryLesson idea.Id)) false ] ]
          | None when not (Settings.hasKey model.Settings) -> label "Writing this lesson needs a Mistral API key (Settings)." 15.0 Palette.muted
          | None -> spinnerLine "Writing the lesson…"
      WrapPanel.create [
          WrapPanel.children [
              match Study.segmentOf r.Script idea with
              | Some seg -> textLink "Listen to it in the paper" (fun () -> dispatch (StudyListen seg)) ("listen", idea.Id)
              | None -> ()
              if ready && not isDone then textLink "I know this: test me" (fun () -> dispatch StudyTestOut) ("testout", idea.Id)
          ]
      ]
      if ready then yield! tutorSection model s false dispatch ]

/// A multiple-choice question: why it is asked, the question, the options, then what the answer means.
let private studyQuiz (model: Model) (r: ReaderState) (s: StudyState) (q: Quiz) (dispatch: Msg -> unit) : IView list =
    let now = DateTime.UtcNow
    let idea = q.Idea |> Option.bind (fun i -> s.Plan |> Option.bind (fun p -> p.Idea i))
    let concept = q.Concept |> Option.bind model.Concepts.TryFind
    let name = idea |> Option.map (fun i -> i.Name) |> Option.orElse (concept |> Option.map (fun c -> c.Name)) |> Option.defaultValue ""
    let right = q.Choice = Some(Some q.Question.Correct)
    let heading =
        match q.Purpose with
        | QuizPurpose.Check -> "CHECK YOUR UNDERSTANDING"
        | QuizPurpose.Retry -> "ANOTHER QUESTION ON IT"
        | QuizPurpose.TestOut -> "TEST YOURSELF"
        | QuizPurpose.QuickCheck -> "QUICK CHECK · YOU STUDIED THIS BEFORE"
        | QuizPurpose.Again -> "AGAIN, A LITTLE LATER"
    [ TextBlock.create [ TextBlock.text heading; TextBlock.fontSize 12.0; TextBlock.fontWeight FontWeight.Bold; TextBlock.foreground Palette.accent ]
      richParagraph name 14.0 Palette.muted
      match q.Purpose, concept with
      | QuizPurpose.QuickCheck, Some c when q.Choice.IsNone ->
          match c.Sources |> List.tryFind (fun x -> x.PaperId <> r.Paper.Id) with
          | Some src -> label (sprintf "You learned it in “%s”. If you still know it, it's skipped here." src.Title) 13.0 Palette.faint
          | None -> ()
      | QuizPurpose.Again, _ when q.Choice.IsNone -> label "Asking again after a few minutes, between other ideas, is what makes it last." 13.0 Palette.faint
      | _ -> ()
      match q.Question.Visual with
      | Some v when q.Question.PaperId = r.Paper.Id -> visualThumb r v 240.0 dispatch
      | Some v -> cardImage q.Question.PaperId v 240.0
      | None -> ()
      richText q.Question.Prompt 20.0 Palette.text
      StackPanel.create [
          StackPanel.spacing 8.0
          StackPanel.children [
              for pos, i in List.indexed q.Order do
                  choiceOption q.Question i pos q.Choice (fun () -> dispatch (StudyChoose(Some i)))
              if q.Choice.IsNone then dontKnow (fun () -> dispatch (StudyChoose None))
          ]
      ]
      match q.Choice with
      | Some choice ->
          let outcome =
              match q.Purpose, right with
              | QuizPurpose.QuickCheck, true -> "Right: you still know it, so it's skipped here."
              | QuizPurpose.QuickCheck, false -> "It has faded, so you'll go through it again next."
              | QuizPurpose.TestOut, true -> "Right: you knew it."
              | QuizPurpose.TestOut, false -> "Not quite. The lesson is next."
              | QuizPurpose.Retry, true -> "Right. It's asked again in a few minutes, to make sure it sticks."
              | _, true -> "Right."
              | _, false when choice.IsNone -> "Fine: now you know. The right answer is in green."
              | _, false -> "Not quite. The right answer is in green."
          StackPanel.create [
              StackPanel.spacing 4.0
              StackPanel.children [
                  label outcome 16.0 (if right then Palette.good else Palette.text)
                  match concept with
                  | Some c when c.Memory.Due > now && not (q.Purpose = QuizPurpose.Retry && right) ->
                      label (sprintf "It comes back in %s." (Fsrs.formatInterval (c.Memory.Due - now))) 13.0 Palette.faint
                  | _ -> ()
              ]
          ]
          WrapPanel.create [
              WrapPanel.children [
                  if q.Purpose = QuizPurpose.TestOut && right then textLink "Read the lesson anyway" (fun () -> dispatch StudyRead) ("read", q.Question.Id)
                  textLink "This question is wrong" (fun () -> dispatch StudyReport) ("report", q.Question.Id)
              ]
          ]
          yield! tutorSection model s (not right) dispatch
      | None -> () ]

/// Explaining a part's ideas in one's own words, and the tutor's feedback.
let private studyRecap (model: Model) (s: StudyState) (plan: Study.Plan) (part: int) (dispatch: Msg -> unit) : IView list =
    let p = plan.Parts.[part]
    let locked = s.Grading || s.Feedback.IsSome
    [ TextBlock.create [
          TextBlock.text (sprintf "EXPLAIN IT BACK · END OF PART %d" (part + 1))
          TextBlock.fontSize 12.0
          TextBlock.fontWeight FontWeight.Bold
          TextBlock.foreground Palette.accent
      ]
      label p.Title 14.0 Palette.muted
      richText p.Recap 20.0 Palette.text
      if not locked then
          label "From memory, in your own words: a few sentences is enough. Putting it together yourself is what makes it stick, so don't look back." 13.0 Palette.faint
      TextBox.create [
          TextBox.text s.RecapInput
          TextBox.watermark "Your answer"
          TextBox.fontSize 16.0
          TextBox.minHeight 130.0
          TextBox.acceptsReturn true
          TextBox.textWrapping TextWrapping.Wrap
          TextBox.cornerRadius 14.0
          TextBox.padding (Thickness(14.0, 10.0))
          TextBox.isReadOnly locked
          TextBox.onTextChanged ((fun t -> if t <> s.RecapInput then dispatch (SetRecapInput t)), SubPatchOptions.OnChangeOf s.RecapInput)
      ]
      if not locked && (Services.get ()).Recorder.IsSome then
          StackPanel.create [
              StackPanel.orientation Orientation.Horizontal
              StackPanel.spacing 10.0
              StackPanel.children [
                  Button.create [
                      Button.width 44.0
                      Button.height 44.0
                      Button.cornerRadius 22.0
                      Button.padding 0.0
                      Button.background (if s.Mic = Mic.Recording then Palette.danger else Palette.surfaceHigh)
                      Button.horizontalContentAlignment HorizontalAlignment.Center
                      Button.verticalContentAlignment VerticalAlignment.Center
                      Button.isEnabled (s.Mic <> Mic.Transcribing)
                      Button.onClick ((fun _ -> dispatch StudyMic), SubPatchOptions.Never)
                      Button.content (if s.Mic = Mic.Recording then icon Icons.stop Palette.onAccent 18.0 true else icon Icons.mic Palette.text 22.0 false)
                  ]
                  label
                      (match s.Mic with
                       | Mic.Recording -> "Listening… tap when you're done."
                       | Mic.Transcribing -> "Writing down what you said…"
                       | Mic.Idle -> "Or say it out loud")
                      14.0 Palette.muted
                  |> fun l -> Border.create [ Border.verticalAlignment VerticalAlignment.Center; Border.child l ]
              ]
          ]
      match s.Feedback with
      | Some f ->
          match f.Verdict with
          | Some Study.Verdict.GotIt -> label "You've got it" 18.0 Palette.good
          | Some Study.Verdict.Partly -> label "Partly there" 18.0 Palette.accent
          | Some Study.Verdict.NotYet -> label "Not yet" 18.0 Palette.danger
          | None -> ()
          richText f.Text 16.0 Palette.text
          if not s.Grading && p.Points <> "" then
              Border.create [
                  Border.background Palette.surface
                  Border.cornerRadius 16.0
                  Border.padding (Thickness(16.0, 12.0, 16.0, 14.0))
                  Border.child (StackPanel.create [ StackPanel.spacing 6.0; StackPanel.children [ sectionLabel "A GOOD ANSWER COVERS"; richText p.Points 15.0 Palette.muted ] ])
              ]
      | None when s.Grading -> spinnerLine "Reading your answer…"
      | None -> () ]

let private studyFinished (model: Model) (r: ReaderState) (s: StudyState) (plan: Study.Plan) : IView list =
    let now = DateTime.UtcNow
    let count (f: Study.Outcome -> bool) = s.Progress.Done |> Map.filter (fun _ o -> f o) |> Map.count
    let learned = count (fun o -> o = Study.Outcome.Learned || o = Study.Outcome.TestedOut)
    let known = count (fun o -> o = Study.Outcome.Known || o = Study.Outcome.Refreshed)
    let next =
        Knowledge.ofPaper r.Paper.Id (model.Concepts |> Map.toList |> List.map snd)
        |> List.map (fun c -> c.Memory.Due)
        |> List.filter (fun d -> d > now)
        |> List.sort
        |> List.tryHead
    [ TextBlock.create [
          TextBlock.text "You've been through the whole paper"
          TextBlock.fontSize 26.0
          TextBlock.fontWeight FontWeight.Bold
          TextBlock.foreground Palette.text
          TextBlock.textWrapping TextWrapping.Wrap
      ]
      label
          ([ sprintf "%d idea%s learned" learned (if learned = 1 then "" else "s")
             if known > 0 then sprintf "%d you knew already" known ]
           |> String.concat " · ")
          16.0 Palette.accent
      label
          ((match next with
            | Some d -> sprintf "What you learned comes back for review just before you'd forget it, the first in %s. " (Fsrs.formatInterval (d - now))
            | None -> "What you learned comes back for review just before you'd forget it. ")
           + "A few minutes a day keeps it for months; each time with a different question.")
          14.0 Palette.muted
      if plan.Overview <> "" then
          sectionLabel "THE PAPER IN SHORT"
          richText plan.Overview 15.0 Palette.muted ]

/// The study session over the reader.
let private studyOverlay (model: Model) (r: ReaderState) (s: StudyState) (dispatch: Msg -> unit) : IView =
    let plan = s.Plan
    let ideas = plan |> Option.map (fun p -> p.Ideas) |> Option.defaultValue []
    let position (id: string) = ideas |> List.tryFindIndex (fun i -> i.Id = id)
    let partTitle (id: string) =
        plan |> Option.bind (fun p -> p.PartOf id |> Option.map (fun i -> sprintf "Part %d · %s" (i + 1) p.Parts.[i].Title))
    let title, subtitle =
        match s.Screen with
        | StudyScreen.Overview -> "Study", r.Script.Title
        | StudyScreen.Teach id ->
            defaultArg (partTitle id) "Study", (match position id with Some i -> sprintf "Idea %d of %d" (i + 1) ideas.Length | None -> "")
        | StudyScreen.Quiz q ->
            match q.Idea with
            | Some id -> defaultArg (partTitle id) "Study", (match position id with Some i -> sprintf "Idea %d of %d" (i + 1) ideas.Length | None -> "")
            | None -> "Quick review", r.Script.Title
        | StudyScreen.Recap part ->
            (match plan with Some p -> sprintf "Part %d · %s" (part + 1) p.Parts.[part].Title | None -> "Study"), "Explain it back"
        | StudyScreen.Finished -> "Study", "Done"
    let fraction = if ideas.IsEmpty then 0.0 else float (min ideas.Length s.Progress.Done.Count) / float ideas.Length
    let body =
        match s.Screen, plan with
        | StudyScreen.Overview, _ -> studyOverview model r s dispatch
        | StudyScreen.Teach id, Some p ->
            match p.Idea id with
            | Some idea -> studyTeach model r s idea dispatch
            | None -> []
        | StudyScreen.Quiz q, _ -> studyQuiz model r s q dispatch
        | StudyScreen.Recap part, Some p -> studyRecap model s p part dispatch
        | StudyScreen.Finished, Some p -> studyFinished model r s p
        | _ -> []
    let row (buttons: IView list) =
        Grid.create [
            Grid.columnDefinitions (String.Join(",", List.replicate buttons.Length "*"))
            Grid.children buttons
        ]
        :> IView
    let buttons: IView list =
        match s.Screen with
        | StudyScreen.Overview ->
            match plan with
            | None when s.Planning.IsNone -> [ wideButton 0 "Start studying" true (Settings.hasKey model.Settings) (fun () -> dispatch StudyStart) ]
            | None -> []
            | Some p ->
                match fst (Study.next DateTime.UtcNow p s.Progress model.Concepts s.Last) with
                | Study.Step.Finished -> [ wideButton 0 "Close" true true (fun () -> dispatch CloseStudy) ]
                | _ -> [ wideButton 0 (if s.Progress.Done.IsEmpty then "Start" else "Continue") true true (fun () -> dispatch StudyStart) ]
        | StudyScreen.Teach id ->
            let ready = s.Lessons.TryFind id |> Option.exists (fun l -> not l.Checks.IsEmpty)
            [ wideButton 0 "Check my understanding" true ready (fun () -> dispatch StudyCheck) ]
        | StudyScreen.Quiz { Choice = None } -> []
        | StudyScreen.Quiz q when q.Choice <> Some(Some q.Question.Correct) && q.Purpose <> QuizPurpose.QuickCheck && q.Purpose <> QuizPurpose.TestOut ->
            [ wideButton 0 "Another question" false true (fun () -> dispatch StudyRetry); wideButton 1 "Continue" true true (fun () -> dispatch StudyContinue) ]
        | StudyScreen.Quiz _ -> [ wideButton 0 "Continue" true true (fun () -> dispatch StudyContinue) ]
        | StudyScreen.Recap _ when s.Feedback.IsSome && not s.Grading -> [ wideButton 0 "Continue" true true (fun () -> dispatch StudyContinue) ]
        | StudyScreen.Recap _ ->
            [ wideButton 0 "Skip" false (not s.Grading) (fun () -> dispatch SkipRecap)
              wideButton 1 "Check my answer" true (not s.Grading && s.RecapInput.Trim() <> "") (fun () -> dispatch SubmitRecap) ]
        | StudyScreen.Finished ->
            let due = reviewQueue model (Some r.Paper.Id) DateTime.UtcNow |> List.length
            [ if due > 0 then wideButton 0 "Review now" false true (fun () -> dispatch (StartReview(Some r.Paper.Id)))
              wideButton (if due > 0 then 1 else 0) "Done" true true (fun () -> dispatch CloseStudy) ]
    Border.create [
        Border.background Palette.bg
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "Auto,*,Auto"
                        Grid.margin (Thickness(4.0, 6.0, 4.0, 0.0))
                        Grid.children [
                            Border.create [ Grid.column 0; Border.child (iconButton Icons.close 22.0 (fun () -> dispatch CloseStudy) "close-study") ]
                            StackPanel.create [
                                Grid.column 1
                                StackPanel.verticalAlignment VerticalAlignment.Center
                                StackPanel.spacing 2.0
                                StackPanel.children [
                                    TextBlock.create [
                                        TextBlock.text title
                                        TextBlock.fontSize 14.0
                                        TextBlock.fontWeight FontWeight.SemiBold
                                        TextBlock.foreground Palette.accent
                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    ]
                                    TextBlock.create [
                                        TextBlock.text subtitle
                                        TextBlock.fontSize 12.0
                                        TextBlock.foreground Palette.muted
                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    ]
                                ]
                            ]
                            match s.Screen, plan with
                            | StudyScreen.Overview, _
                            | _, None -> ()
                            | _ -> Border.create [ Grid.column 2; Border.child (iconButton Icons.list 22.0 (fun () -> dispatch StudyShowOverview) "study-overview") ]
                        ]
                    ]
                    ProgressBar.create [
                        DockPanel.dock Dock.Top
                        ProgressBar.margin (Thickness(20.0, 6.0, 20.0, 4.0))
                        ProgressBar.minimum 0.0
                        ProgressBar.maximum 1.0
                        ProgressBar.value fraction
                        ProgressBar.height 4.0
                        ProgressBar.minHeight 4.0
                        ProgressBar.cornerRadius 2.0
                        ProgressBar.foreground Palette.accent
                        ProgressBar.background Palette.surfaceHigh
                        ProgressBar.isVisible plan.IsSome
                    ]
                    if not buttons.IsEmpty || s.Error.IsSome then
                        Border.create [
                            DockPanel.dock Dock.Bottom
                            Border.background Palette.surface
                            Border.cornerRadius (24.0, 24.0, 0.0, 0.0)
                            Border.padding (Thickness(13.0, 14.0, 13.0, 16.0))
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 10.0
                                    StackPanel.children [
                                        match s.Error with
                                        | Some e -> Border.create [ Border.margin (Thickness(4.0, 0.0)); Border.child (label e 14.0 Palette.danger) ]
                                        | None -> ()
                                        if not buttons.IsEmpty then row buttons
                                    ]
                                ]
                            )
                        ]
                    // a new screen starts at the top
                    View.withKey (sprintf "study-%s" (match s.Screen with
                                                      | StudyScreen.Overview -> "overview"
                                                      | StudyScreen.Teach i -> "teach-" + i
                                                      | StudyScreen.Quiz q -> "quiz-" + q.Question.Id
                                                      | StudyScreen.Recap p -> sprintf "recap-%d" p
                                                      | StudyScreen.Finished -> "finished")) (
                    ScrollViewer.create [
                        ScrollViewer.content (
                            StackPanel.create [
                                StackPanel.margin (Thickness(22.0, 12.0, 22.0, 28.0))
                                StackPanel.spacing 16.0
                                StackPanel.children body
                            ]
                        )
                    ])
                ]
            ]
        )
    ]

// ---------------------------------------------------------------------------------------------
// Walking mode: the simple player, big buttons and the equation large
// ---------------------------------------------------------------------------------------------

/// Display size of a crop pixel in walking mode: larger than the full player, glanced at from arm's length.
let private walkMathScale = 1.3

let private sectionName (r: ReaderState) (seg: Segment) =
    if seg.Section < r.Script.Sections.Length && seg.Section > 0 then r.Script.Sections.[seg.Section].Title else "Beginning"

/// A button big enough to hit while walking, icon above its word.
let private walkButton (column: int) (height: float) (data: string) (text: string) (primary: bool) (key: obj) (onClick: unit -> unit) : IView =
    let fg = if primary then Palette.onAccent else Palette.text
    Button.create [
        Grid.column column
        Button.height height
        Button.margin (Thickness(5.0, 0.0))
        Button.cornerRadius 24.0
        Button.horizontalAlignment HorizontalAlignment.Stretch
        Button.horizontalContentAlignment HorizontalAlignment.Center
        Button.verticalContentAlignment VerticalAlignment.Center
        Button.background (if primary then Palette.accent else Palette.surfaceHigh)
        Button.onClick ((fun _ -> onClick ()), SubPatchOptions.OnChangeOf key)
        Button.content (
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.children [
                    Border.create [
                        Border.horizontalAlignment HorizontalAlignment.Center
                        Border.child (icon data (if primary then fg else Palette.accent) (if primary then 40.0 else 30.0) primary)
                    ]
                    TextBlock.create [
                        TextBlock.text text
                        TextBlock.fontSize (if primary then 22.0 else 16.0)
                        TextBlock.fontWeight FontWeight.SemiBold
                        TextBlock.foreground fg
                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                    ]
                ]
            ]
        )
    ]

/// The equation or figure as large as fits (tap for full size), or else the sentence being spoken in large type.
let private walkingStage (r: ReaderState) (seg: Segment) (dispatch: Msg -> unit) : IView =
    let paths = Store.Paths((Services.get ()).DataDir)
    let visual = (match r.Held with Some v -> Some v | None -> seg.Show) |> Option.bind r.Script.Visual
    let image = visual |> Option.bind (fun v -> bitmap (paths.Image(r.Paper.Id, v.Id)) |> Option.map (fun b -> v, b))
    match image with
    | Some (v, bmp) ->
        Border.create [
            Border.margin (Thickness(5.0, 4.0, 5.0, 12.0))
            Border.padding (Thickness(12.0, 8.0, 12.0, 12.0))
            Border.cornerRadius 20.0
            Border.background Palette.paper
            Border.verticalAlignment VerticalAlignment.Center
            Border.onTapped ((fun _ -> dispatch ToggleZoom), SubPatchOptions.Never)
            Border.child (
                Grid.create [
                    Grid.rowDefinitions "Auto,*"
                    Grid.children [
                        TextBlock.create [
                            Grid.row 0
                            TextBlock.text (if r.Held.IsSome then sprintf "%s · stopped here" (visualName v) else visualName v)
                            TextBlock.fontSize 13.0
                            TextBlock.foreground Palette.ink
                            TextBlock.margin (Thickness(0.0, 0.0, 0.0, 6.0))
                            TextBlock.textTrimming TextTrimming.CharacterEllipsis
                        ]
                        Image.create [
                            Grid.row 1
                            Image.source bmp
                            Image.stretch Stretch.Uniform
                            Image.maxWidth (float bmp.PixelSize.Width * walkMathScale)
                            Image.maxHeight (float bmp.PixelSize.Height * walkMathScale)
                            Image.horizontalAlignment HorizontalAlignment.Center
                            Image.verticalAlignment VerticalAlignment.Center
                        ]
                    ]
                ]
            )
        ]
    | None ->
        let heading = seg.Kind = UnitKind.Heading || seg.Kind = UnitKind.Title
        ScrollViewer.create [
            ScrollViewer.content (
                TextBlock.create [
                    TextBlock.margin (Thickness(10.0, 12.0, 10.0, 12.0))
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    TextBlock.text seg.Say
                    TextBlock.fontSize (if heading then 32.0 else 27.0)
                    TextBlock.lineHeight (if heading then 40.0 else 38.0)
                    TextBlock.fontWeight (if heading then FontWeight.Bold else FontWeight.Medium)
                    TextBlock.foreground (if heading then Palette.accent else Palette.text)
                    TextBlock.textWrapping TextWrapping.Wrap
                ]
            )
        ]

/// The simple player for listening on the move: one huge play / pause / continue button within thumb's reach,
/// three large ones above it, and the equation on screen as big as it goes.
let private walkingOverlay (model: Model) (r: ReaderState) (dispatch: Msg -> unit) : IView =
    let seg = r.Script.Segments.[r.Current]
    let again = r.Held |> Option.bind (firstReading r.Script)
    Border.create [
        Border.background Palette.bg
        Border.padding (Thickness(11.0, 8.0, 11.0, 16.0))
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "Auto,*,Auto"
                        Grid.margin (Thickness(5.0, 0.0, 5.0, 4.0))
                        Grid.children [
                            Button.create [
                                Grid.column 0
                                Button.height 52.0
                                Button.cornerRadius 26.0
                                Button.padding (Thickness(14.0, 0.0, 18.0, 0.0))
                                Button.verticalContentAlignment VerticalAlignment.Center
                                Button.background Palette.surfaceHigh
                                Button.onClick ((fun _ -> dispatch (SetWalking false)), SubPatchOptions.Never)
                                Button.content (
                                    StackPanel.create [
                                        StackPanel.orientation Orientation.Horizontal
                                        StackPanel.spacing 8.0
                                        StackPanel.children [
                                            icon Icons.close Palette.text 20.0 false
                                            TextBlock.create [
                                                TextBlock.text "Exit"
                                                TextBlock.fontSize 16.0
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Palette.text
                                                TextBlock.verticalAlignment VerticalAlignment.Center
                                            ]
                                        ]
                                    ]
                                )
                            ]
                            StackPanel.create [
                                Grid.column 1
                                StackPanel.margin (Thickness(12.0, 0.0))
                                StackPanel.verticalAlignment VerticalAlignment.Center
                                StackPanel.spacing 2.0
                                StackPanel.children [
                                    TextBlock.create [
                                        TextBlock.text (sectionName r seg)
                                        TextBlock.fontSize 16.0
                                        TextBlock.fontWeight FontWeight.SemiBold
                                        TextBlock.foreground Palette.accent
                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    ]
                                    TextBlock.create [
                                        TextBlock.text (
                                            if r.Finished then "Finished"
                                            else sprintf "%s %d of %d · %s" (capitalize (Formats.pageNoun r.Paper.Format)) (seg.Page + 1) r.Script.PageCount (formatMinutes (remainingMs r model.Settings.Speed)))
                                        TextBlock.fontSize 13.0
                                        TextBlock.foreground Palette.muted
                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    ]
                                ]
                            ]
                            Button.create [
                                Grid.column 2
                                Button.height 52.0
                                Button.minWidth 76.0
                                Button.cornerRadius 26.0
                                Button.horizontalContentAlignment HorizontalAlignment.Center
                                Button.verticalContentAlignment VerticalAlignment.Center
                                Button.background Palette.surfaceHigh
                                Button.foreground Palette.text
                                Button.fontSize 18.0
                                Button.fontWeight FontWeight.SemiBold
                                Button.content (sprintf "%g×" model.Settings.Speed)
                                Button.onClick ((fun _ -> dispatch CycleSpeed), SubPatchOptions.Never)
                            ]
                        ]
                    ]
                    StackPanel.create [
                        DockPanel.dock Dock.Bottom
                        StackPanel.spacing 12.0
                        StackPanel.children [
                            Border.create [
                                Border.margin (Thickness(5.0, 0.0))
                                Border.child (StackPanel.create [ StackPanel.spacing 10.0; StackPanel.children [ yield! playerStatus model r dispatch; progressBar r ] ])
                            ]
                            Grid.create [
                                Grid.columnDefinitions (if again.IsSome then "*,*" else "*,*,*")
                                Grid.children [
                                    match again with
                                    | Some i ->
                                        // stopped at an equation: hear it once more, or ask about it
                                        walkButton 0 88.0 Icons.back "Hear it again" false (box ("again", i)) (fun () -> dispatch (JumpToSegment i))
                                        walkButton 1 88.0 Icons.ask "Ask" false (box "ask") (fun () -> dispatch (OpenHelp r.Held))
                                    | None ->
                                        walkButton 0 88.0 Icons.back "Back 15 s" false (box "back") (fun () -> dispatch Back15)
                                        walkButton 1 88.0 Icons.ask "Ask" false (box "ask") (fun () -> dispatch (OpenHelp r.Held))
                                        walkButton 2 88.0 Icons.forward "Skip 15 s" false (box "forward") (fun () -> dispatch Forward15)
                                ]
                            ]
                            Grid.create [
                                Grid.children [
                                    let data, text =
                                        if r.Playing then Icons.pause, "Pause"
                                        elif r.Held.IsSome then Icons.play, "Continue"
                                        elif r.Finished then Icons.play, "Play again"
                                        else Icons.play, "Play"
                                    walkButton 0 128.0 data text true (box "play") (fun () -> dispatch TogglePlay)
                                ]
                            ]
                        ]
                    ]
                    walkingStage r seg dispatch
                ]
            ]
        )
    ]

let private readerView (model: Model) (r: ReaderState) (dispatch: Msg -> unit) : IView =
    let seg = r.Script.Segments.[r.Current]
    let sectionTitle = sectionName r seg
    Grid.create [
        Grid.children [
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "Auto,*,Auto,Auto,Auto,Auto,Auto"
                        Grid.margin (Thickness(4.0, 6.0, 4.0, 2.0))
                        Grid.children [
                            Border.create [ Grid.column 0; Border.child (iconButton Icons.chevronLeft 24.0 (fun () -> dispatch CloseReader) "close-reader") ]
                            StackPanel.create [
                                Grid.column 1
                                StackPanel.verticalAlignment VerticalAlignment.Center
                                StackPanel.spacing 2.0
                                StackPanel.children [
                                    TextBlock.create [
                                        TextBlock.text sectionTitle
                                        TextBlock.fontSize 14.0
                                        TextBlock.fontWeight FontWeight.SemiBold
                                        TextBlock.foreground Palette.accent
                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    ]
                                    TextBlock.create [
                                        TextBlock.text r.Script.Title
                                        TextBlock.fontSize 12.0
                                        TextBlock.foreground Palette.muted
                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    ]
                                ]
                            ]
                            Button.create [
                                Grid.column 2
                                Button.height 38.0
                                Button.cornerRadius 19.0
                                Button.padding (Thickness(12.0, 0.0, 14.0, 0.0))
                                Button.margin (Thickness(6.0, 0.0, 2.0, 0.0))
                                Button.verticalAlignment VerticalAlignment.Center
                                Button.verticalContentAlignment VerticalAlignment.Center
                                Button.background Palette.surfaceHigh
                                Button.onClick ((fun _ -> dispatch (OpenHelp None)), SubPatchOptions.Never)
                                Button.content (
                                    StackPanel.create [
                                        StackPanel.orientation Orientation.Horizontal
                                        StackPanel.spacing 6.0
                                        StackPanel.children [
                                            icon Icons.ask Palette.accent 20.0 false
                                            TextBlock.create [
                                                TextBlock.text "Ask"
                                                TextBlock.fontSize 15.0
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Palette.text
                                                TextBlock.verticalAlignment VerticalAlignment.Center
                                            ]
                                        ]
                                    ]
                                )
                            ]
                            Border.create [ Grid.column 3; Border.child (iconButton Icons.walk 22.0 (fun () -> dispatch (SetWalking true)) "walking") ]
                            Border.create [ Grid.column 4; Border.child (iconButton Icons.cards 22.0 (fun () -> dispatch (OpenCards None)) "cards") ]
                            Border.create [ Grid.column 5; Border.child (iconButton Icons.sigma 22.0 (fun () -> dispatch ToggleEquations) "equations") ]
                            Border.create [ Grid.column 6; Border.child (iconButton Icons.sliders 22.0 (fun () -> dispatch (SetShowSettings true)) "reader-settings") ]
                        ]
                    ]
                    Border.create [ DockPanel.dock Dock.Bottom; Border.child (controls model r dispatch) ]
                    stage r seg dispatch
                ]
            ]
            if model.Settings.WalkingMode then walkingOverlay model r dispatch
            if r.ShowOutline then outlineOverlay r dispatch
            if r.ShowEquations then equationsOverlay r dispatch
            match r.Study with
            | Some s when s.Hidden ->
                // listening from the study session: one tap goes back to it
                Button.create [
                    Button.horizontalAlignment HorizontalAlignment.Center
                    Button.verticalAlignment VerticalAlignment.Top
                    Button.margin (Thickness(0.0, 64.0, 0.0, 0.0))
                    Button.height 44.0
                    Button.cornerRadius 22.0
                    Button.padding (Thickness(16.0, 0.0, 20.0, 0.0))
                    Button.background Palette.accent
                    Button.verticalContentAlignment VerticalAlignment.Center
                    Button.onClick ((fun _ -> dispatch StudyBack), SubPatchOptions.Never)
                    Button.content (
                        StackPanel.create [
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 8.0
                            StackPanel.children [
                                icon Icons.study Palette.onAccent 20.0 false
                                TextBlock.create [
                                    TextBlock.text "Back to studying"
                                    TextBlock.fontSize 15.0
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground Palette.onAccent
                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                ]
                            ]
                        ]
                    )
                ]
            | _ -> ()
            match r.Help with
            | Some h -> helpOverlay model r h dispatch
            | None -> ()
            match r.Cards with
            | Some c -> cardsOverlay model r c dispatch
            | None -> ()
            match r.Study with
            | Some s when not s.Hidden -> studyOverlay model r s dispatch
            | _ -> ()
            match r.Zoom with
            | Some v -> zoomOverlay r v dispatch
            | None -> ()
        ]
    ]

// ---------------------------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------------------------

let private sectionTitle (text: string) : IView =
    TextBlock.create [
        TextBlock.text text
        TextBlock.fontSize 13.0
        TextBlock.fontWeight FontWeight.Bold
        TextBlock.foreground Palette.accent
        TextBlock.margin (Thickness(0.0, 18.0, 0.0, 2.0))
    ]

let private toggle (title: string) (description: string) (value: bool) (onChange: bool -> unit) : IView =
    Grid.create [
        Grid.columnDefinitions "*,Auto"
        Grid.children [
            StackPanel.create [
                Grid.column 0
                StackPanel.spacing 3.0
                StackPanel.children [ label title 16.0 Palette.text; label description 13.0 Palette.muted ]
            ]
            ToggleSwitch.create [
                Grid.column 1
                ToggleSwitch.isChecked value
                ToggleSwitch.onContent ""
                ToggleSwitch.offContent ""
                ToggleSwitch.margin (Thickness(12.0, 0.0, 0.0, 0.0))
                ToggleSwitch.verticalAlignment VerticalAlignment.Center
                ToggleSwitch.onIsCheckedChanged (
                    (fun e ->
                        match e.Source with
                        | :? ToggleSwitch as t -> onChange (t.IsChecked.GetValueOrDefault())
                        | _ -> ()),
                    SubPatchOptions.OnChangeOf title)
            ]
        ]
    ]

let private settingsView (model: Model) (dispatch: Msg -> unit) : IView =
    let s = model.Settings
    Border.create [
        Border.background Palette.bg
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "*,Auto"
                        Grid.margin (Thickness(20.0, 14.0, 12.0, 4.0))
                        Grid.children [
                            TextBlock.create [
                                Grid.column 0
                                TextBlock.text "Settings"
                                TextBlock.fontSize 26.0
                                TextBlock.fontWeight FontWeight.Bold
                                TextBlock.foreground Palette.text
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                            Border.create [ Grid.column 1; Border.child (pill "Done" (fun () -> dispatch (SetShowSettings false)) true) ]
                        ]
                    ]
                    ScrollViewer.create [
                        ScrollViewer.content (
                            StackPanel.create [
                                StackPanel.margin (Thickness(20.0, 0.0, 20.0, 32.0))
                                StackPanel.spacing 12.0
                                StackPanel.children [
                                    sectionTitle "LISTENING"
                                    toggle "Stop at equations" "Pause once an equation or algorithm has been read and explained, with it on screen, until you tap Continue." s.StopAtEquations (SetStopAtEquations >> dispatch)
                                    toggle "Stop at figures and tables" "The same for figures and tables, after they are first shown and discussed." s.StopAtFigures (SetStopAtFigures >> dispatch)
                                    sectionTitle "ASK AND STUDY"
                                    label "About you" 16.0 Palette.text
                                    TextBox.create [
                                        TextBox.text s.AboutMe
                                        TextBox.watermark "e.g. biology PhD student, rusty on linear algebra"
                                        TextBox.fontSize 15.0
                                        TextBox.textWrapping TextWrapping.Wrap
                                        TextBox.onTextChanged ((fun t -> if t <> model.Settings.AboutMe then dispatch (SetAboutMe t)), SubPatchOptions.OnChangeOf s.AboutMe)
                                    ]
                                    label "Optional. Answers, lessons and the ideas taught as background are pitched at this level." 12.0 Palette.faint
                                    label "Model for Ask, cards and Study" 14.0 Palette.muted
                                    TextBox.create [
                                        TextBox.text s.HelpModel
                                        TextBox.fontSize 15.0
                                        TextBox.onTextChanged ((fun t -> if t <> model.Settings.HelpModel then dispatch (SetHelpModel t)), SubPatchOptions.OnChangeOf s.HelpModel)
                                    ]
                                    label "zai-glm-5-3 (GLM 5.3, hosted by Mistral) reads the whole paper for every answer, card and lesson." 12.0 Palette.faint
                                    sectionTitle "LEARN"
                                    label "Remember" 16.0 Palette.text
                                    WrapPanel.create [
                                        WrapPanel.children [
                                            for r, text in [ 0.85, "85% · fewer reviews"; 0.9, "90%"; 0.95, "95% · more reviews" ] ->
                                                let selected = abs (s.Retention - r) < 0.001
                                                Button.create [
                                                    Button.content text
                                                    Button.fontSize 14.0
                                                    Button.padding (Thickness(14.0, 8.0))
                                                    Button.margin (Thickness(0.0, 0.0, 8.0, 8.0))
                                                    Button.cornerRadius 18.0
                                                    Button.background (if selected then Palette.accent else Palette.surfaceHigh)
                                                    Button.foreground (if selected then Palette.onAccent else Palette.text)
                                                    Button.onClick ((fun _ -> dispatch (SetRetention r)), SubPatchOptions.OnChangeOf(r, selected))
                                                ]
                                        ]
                                    ]
                                    label "How much you want to still know when a card or idea comes back. Reviews are scheduled with FSRS, which shows each one again just before you would forget it. A paper you study skips the ideas you know above this, and checks the ones below it with a quick question." 12.0 Palette.faint
                                    sectionTitle "FIND PAPERS"
                                    label "OpenAlex key" 16.0 Palette.text
                                    TextBox.create [
                                        TextBox.text s.OpenAlexKey
                                        TextBox.passwordChar '•'
                                        TextBox.watermark "Optional, free from openalex.org"
                                        TextBox.fontSize 15.0
                                        TextBox.onTextChanged ((fun t -> if t <> model.Settings.OpenAlexKey then dispatch (SetOpenAlexKey t)), SubPatchOptions.OnChangeOf s.OpenAlexKey)
                                    ]
                                    label "Searching works without a key for about 100 searches a day. A free key raises that. Stored only on this device and sent only to api.openalex.org." 12.0 Palette.faint
                                    sectionTitle "MISTRAL AI"
                                    label "API key" 16.0 Palette.text
                                    TextBox.create [
                                        TextBox.text s.MistralApiKey
                                        TextBox.passwordChar '•'
                                        TextBox.watermark "Paste your key from console.mistral.ai"
                                        TextBox.fontSize 15.0
                                        TextBox.onTextChanged ((fun t -> if t <> model.Settings.MistralApiKey then dispatch (SetApiKey t)), SubPatchOptions.OnChangeOf s.MistralApiKey)
                                    ]
                                    label "Stored only on this device and sent only to api.mistral.ai. With a key, Mistral OCR also checks every equation (so none is shown cut off) and finds the figures and tables, shown when the text refers to them." 12.0 Palette.faint
                                    toggle "Explain with Mistral" "A Mistral model rewrites each paper for listening: it reads formulas the way a lecturer would, walks through every equation and algorithm, and removes citation clutter." s.UseMistralNarration (SetNarration >> dispatch)
                                    label "Model" 14.0 Palette.muted
                                    TextBox.create [
                                        TextBox.text s.NarrationModel
                                        TextBox.fontSize 15.0
                                        TextBox.onTextChanged ((fun t -> if t <> model.Settings.NarrationModel then dispatch (SetNarrationModel t)), SubPatchOptions.OnChangeOf s.NarrationModel)
                                    ]
                                    label "mistral-medium-latest works well; mistral-large-latest is more thorough, mistral-small-latest is faster. Applies to papers you add next." 12.0 Palette.faint
                                    toggle "Mistral voice" "Read aloud with Voxtral. Off: the device's own text-to-speech voice." s.UseMistralVoice (SetMistralVoice >> dispatch)
                                    if s.UseMistralVoice then
                                        sectionTitle "VOICE"
                                        Grid.create [
                                            Grid.columnDefinitions "*,Auto"
                                            Grid.children [
                                                StackPanel.create [
                                                    Grid.column 0
                                                    StackPanel.verticalAlignment VerticalAlignment.Center
                                                    StackPanel.children [ label s.VoiceName 16.0 Palette.text; label s.VoiceId 12.0 Palette.faint ]
                                                ]
                                                Border.create [ Grid.column 1; Border.child (pill "Load voices" (fun () -> dispatch LoadVoices) false) ]
                                            ]
                                        ]
                                        match model.VoicesStatus with
                                        | Some st -> label st 13.0 Palette.muted
                                        | None -> ()
                                        for v in model.Voices do
                                            let selected = v.Id = s.VoiceId
                                            plainButton (if selected then Palette.surfaceHigh else Palette.surface) [
                                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                                Button.horizontalContentAlignment HorizontalAlignment.Left
                                                Button.padding (Thickness(14.0, 10.0))
                                                Button.cornerRadius 12.0
                                                Button.onClick ((fun _ -> dispatch (PickVoice v)), SubPatchOptions.OnChangeOf v.Id)
                                                Button.content (
                                                    StackPanel.create [
                                                        StackPanel.children [
                                                            label v.Name 15.0 (if selected then Palette.accent else Palette.text)
                                                            label (String.Join(", ", v.Languages)) 12.0 Palette.faint
                                                        ]
                                                    ]
                                                )
                                            ]
                                    sectionTitle "ABOUT THE CACHE"
                                    label "Each paper is analysed and narrated once. Audio is generated just ahead of where you listen and kept per voice, so replays, jumps back and re-opening never generate it again." 13.0 Palette.muted
                                ]
                            ]
                        )
                    ]
                ]
            ]
        )
    ]

// ---------------------------------------------------------------------------------------------
// Root
// ---------------------------------------------------------------------------------------------

let private notice (text: string) (dispatch: Msg -> unit) : IView =
    Border.create [
        Border.verticalAlignment VerticalAlignment.Top
        Border.margin (Thickness(12.0, 12.0, 12.0, 0.0))
        Border.padding (Thickness(16.0, 12.0))
        Border.cornerRadius 14.0
        Border.background "#3A2A1C"
        Border.onTapped ((fun _ -> dispatch Dismiss), SubPatchOptions.Never)
        Border.child (
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.children [ label text 14.0 "#FFD8B0"; label "Tap to dismiss" 11.0 "#B08A66" ]
            ]
        )
    ]

/// Set on every render so the Android back button knows whether the app handles it.
let mutable canGoBack = false

/// Set on every render: a review is open, so keys answer cards instead of controlling playback.
let mutable reviewing = false

/// Set on every render, for the W key: a paper is open, and whether it shows the walking player.
let mutable inReader = false
let mutable walking = false

/// Set on every render: a study session is on screen, so keys answer its questions instead of controlling playback.
let mutable studying = false

let view (model: Model) (dispatch: Msg -> unit) : IView =
    canGoBack <- State.canGoBack model
    reviewing <- model.Review.IsSome
    inReader <- (match model.Screen with Screen.Reader _ -> true | _ -> false)
    walking <- model.Settings.WalkingMode
    studying <- (match model.Screen with Screen.Reader { Study = Some s } -> not s.Hidden && model.Review.IsNone | _ -> false)
    Grid.create [
        Grid.background Palette.bg
        Grid.children [
            match model.Screen with
            | Screen.Library -> libraryView model dispatch
            | Screen.Discover -> discoverView model dispatch
            | Screen.Learn -> learnView model dispatch
            | Screen.Importing s -> importingView s dispatch
            | Screen.Reader r -> readerView model r dispatch
            match model.Review with
            | Some rv -> reviewOverlay model rv dispatch
            | None -> ()
            if model.ShowSettings then settingsView model dispatch
            match model.Notice with
            | Some n -> notice n dispatch
            | None -> ()
        ]
    ]
