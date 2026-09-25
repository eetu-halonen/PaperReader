module PaperReader.Views

open System
open System.Collections.Generic
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Shapes
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
            while recent.Count > 24 do
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
                            label (sprintf "%d pages%s" p.PageCount progress) 13.0 Palette.muted
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
                    label "Mistral rewrites the paper for listening, explains every equation out loud, and reads it with a natural voice. Without a key, the phone's own voice reads the text directly." 13.0 Palette.muted
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.children [ pill "Open settings" (fun () -> dispatch (SetShowSettings true)) false ]
                    ]
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
                            label "Listen to papers. The math appears on screen." 14.0 Palette.muted
                        ]
                    ]
                    Border.create [
                        Grid.column 1
                        Border.verticalAlignment VerticalAlignment.Top
                        Border.child (iconButton Icons.sliders 22.0 (fun () -> dispatch (SetShowSettings true)) "settings")
                    ]
                ]
            ]
            Button.create [
                DockPanel.dock Dock.Bottom
                Button.margin (Thickness(16.0, 8.0, 16.0, 16.0))
                Button.height 58.0
                Button.cornerRadius 29.0
                Button.horizontalAlignment HorizontalAlignment.Stretch
                Button.horizontalContentAlignment HorizontalAlignment.Center
                Button.verticalContentAlignment VerticalAlignment.Center
                Button.background Palette.accent
                Button.onClick (fun _ -> dispatch OpenPdf)
                Button.content (
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 10.0
                        StackPanel.children [
                            icon Icons.plus Palette.onAccent 20.0 false
                            TextBlock.create [
                                TextBlock.text "Open a PDF"
                                TextBlock.fontSize 17.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground Palette.onAccent
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                        ]
                    ]
                )
            ]
            ScrollViewer.create [
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.children [
                            if not (Settings.hasKey model.Settings) then keyHint dispatch
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
                                            TextBlock.text "Open a PDF, or share one to Paper Reader from another app. It is prepared once and kept on the phone, so it opens instantly afterwards."
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
            label "This happens once. The narration, equation images and voice are cached on the phone." 13.0 Palette.faint
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.children [ pill "Cancel" (fun () -> dispatch CancelImport) false ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------------------------
// Reader
// ---------------------------------------------------------------------------------------------

let private visualCaption (script: Script) (seg: Segment) (v: Visual) =
    let name =
        match v.Kind, v.EqNumber with
        | VisualKind.Algorithm, Some n -> sprintf "Algorithm %s" n
        | VisualKind.Algorithm, None -> "Algorithm"
        | VisualKind.Equation, Some n when n.Contains "–" -> sprintf "Equations (%s)" n
        | VisualKind.Equation, Some n -> sprintf "Equation (%s)" n
        | VisualKind.Equation, None -> "Equation"
        | VisualKind.Inline, _ -> "From the text"
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

let private stage (r: ReaderState) (seg: Segment) (dispatch: Msg -> unit) : IView =
    let paths = Store.Paths((Services.get ()).DataDir)
    let visual = seg.Show |> Option.bind r.Script.Visual
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
                                            TextBlock.text (visualCaption r.Script seg v)
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
                                    Image.horizontalAlignment HorizontalAlignment.Center
                                    Image.verticalAlignment VerticalAlignment.Center
                                ]
                            ]
                        ]
                    )
                ]
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

let private controls (model: Model) (r: ReaderState) (dispatch: Msg -> unit) : IView =
    let segs = r.Script.Segments
    let seg = segs.[r.Current]
    let fraction =
        let d = r.Durations.TryFind r.Current |> Option.defaultValue 1
        (float r.Current + min 1.0 (float r.Offset / float (max 1 d))) / float (max 1 segs.Length)
    let sectionCount = max 1 (r.Script.Sections.Length - 1)
    let usingMistralVoice = Settings.hasKey model.Settings && model.Settings.UseMistralVoice
    Border.create [
        Border.background Palette.surface
        Border.cornerRadius (24.0, 24.0, 0.0, 0.0)
        Border.padding (Thickness(18.0, 14.0, 18.0, 18.0))
        Border.child (
            StackPanel.create [
                StackPanel.spacing 10.0
                StackPanel.children [
                    match r.Error, r.Waiting with
                    | Some e, _ ->
                        StackPanel.create [
                            StackPanel.spacing 8.0
                            StackPanel.children [
                                label e 13.0 Palette.danger
                                if usingMistralVoice then
                                    StackPanel.create [
                                        StackPanel.orientation Orientation.Horizontal
                                        StackPanel.spacing 8.0
                                        StackPanel.children [
                                            pill "Use the phone's voice" (fun () -> dispatch UsePhoneVoice) false
                                            pill "Settings" (fun () -> dispatch (SetShowSettings true)) false
                                        ]
                                    ]
                            ]
                        ]
                    | None, true ->
                        StackPanel.create [
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
                        ]
                    | None, false -> ()
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
                    Grid.create [
                        Grid.columnDefinitions "*,Auto"
                        Grid.children [
                            TextBlock.create [
                                Grid.column 0
                                TextBlock.text (
                                    if r.Finished then "Finished"
                                    else sprintf "Section %d of %d · page %d of %d" (max 1 seg.Section) sectionCount (seg.Page + 1) r.Script.PageCount)
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

let private zoomOverlay (r: ReaderState) (seg: Segment) (dispatch: Msg -> unit) : IView =
    let paths = Store.Paths((Services.get ()).DataDir)
    let bmp = seg.Show |> Option.bind (fun v -> bitmap (paths.Image(r.Paper.Id, v)))
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

let private readerView (model: Model) (r: ReaderState) (dispatch: Msg -> unit) : IView =
    let seg = r.Script.Segments.[r.Current]
    let sectionTitle =
        if seg.Section < r.Script.Sections.Length && seg.Section > 0 then r.Script.Sections.[seg.Section].Title else "Beginning"
    Grid.create [
        Grid.children [
            DockPanel.create [
                DockPanel.children [
                    Grid.create [
                        DockPanel.dock Dock.Top
                        Grid.columnDefinitions "Auto,*,Auto"
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
                            Border.create [ Grid.column 2; Border.child (iconButton Icons.sliders 22.0 (fun () -> dispatch (SetShowSettings true)) "reader-settings") ]
                        ]
                    ]
                    Border.create [ DockPanel.dock Dock.Bottom; Border.child (controls model r dispatch) ]
                    stage r seg dispatch
                ]
            ]
            if r.ShowOutline then outlineOverlay r dispatch
            if r.Zoomed && seg.Show.IsSome then zoomOverlay r seg dispatch
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
                                    sectionTitle "MISTRAL AI"
                                    label "API key" 16.0 Palette.text
                                    TextBox.create [
                                        TextBox.text s.MistralApiKey
                                        TextBox.passwordChar '•'
                                        TextBox.watermark "Paste your key from console.mistral.ai"
                                        TextBox.fontSize 15.0
                                        TextBox.onTextChanged ((fun t -> if t <> model.Settings.MistralApiKey then dispatch (SetApiKey t)), SubPatchOptions.OnChangeOf s.MistralApiKey)
                                    ]
                                    label "Stored only on this phone and sent only to api.mistral.ai." 12.0 Palette.faint
                                    toggle "Explain with Mistral" "A Mistral model rewrites each paper for listening: it reads formulas the way a lecturer would, walks through every equation and algorithm, and removes citation clutter." s.UseMistralNarration (SetNarration >> dispatch)
                                    label "Model" 14.0 Palette.muted
                                    TextBox.create [
                                        TextBox.text s.NarrationModel
                                        TextBox.fontSize 15.0
                                        TextBox.onTextChanged ((fun t -> if t <> model.Settings.NarrationModel then dispatch (SetNarrationModel t)), SubPatchOptions.OnChangeOf s.NarrationModel)
                                    ]
                                    label "mistral-medium-latest works well; mistral-large-latest is more thorough, mistral-small-latest is faster. Applies to papers you add next." 12.0 Palette.faint
                                    toggle "Mistral voice" "Read aloud with Voxtral. Off: the phone's own text-to-speech voice." s.UseMistralVoice (SetMistralVoice >> dispatch)
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

let view (model: Model) (dispatch: Msg -> unit) : IView =
    canGoBack <- State.canGoBack model
    Grid.create [
        Grid.background Palette.bg
        Grid.children [
            match model.Screen with
            | Screen.Library -> libraryView model dispatch
            | Screen.Importing s -> importingView s dispatch
            | Screen.Reader r -> readerView model r dispatch
            if model.ShowSettings then settingsView model dispatch
            match model.Notice with
            | Some n -> notice n dispatch
            | None -> ()
        ]
    ]
