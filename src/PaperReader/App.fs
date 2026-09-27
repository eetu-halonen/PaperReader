namespace PaperReader

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Elmish
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Elmish

/// The single view hosting the Elmish program.
type MainView() as this =
    inherit HostControl()

    let mutable dispatch: (State.Msg -> unit) option = None
    /// The top level whose events are hooked up (the view can be attached again, e.g. after an activity restart).
    let mutable hooked: TopLevel = null

    do
        this.Background <- Media.SolidColorBrush(Media.Color.Parse Views.Palette.bg)
        // some layouts follow the app's size: turning the phone, resizing the window
        this.SizeChanged.Add(fun e -> dispatch |> Option.iter (fun d -> d (State.Resized(e.NewSize.Width, e.NewSize.Height))))
        Program.mkProgram State.init State.update Views.view
        |> Program.withHost this
        |> Program.withSubscription (fun model ->
            // a permanent subscription hands out dispatch for events coming from outside Elmish
            [ [ "bridge" ], fun d -> dispatch <- Some d; { new IDisposable with member _.Dispose() = () } ]
            @ State.subscriptions model)
        |> Program.runWithAvaloniaSyncDispatch ()

    override this.OnAttachedToVisualTree(e) =
        base.OnAttachedToVisualTree e
        let top = TopLevel.GetTopLevel this
        if not (isNull top) && not (obj.ReferenceEquals(top, hooked)) then
            hooked <- top
            Services.topLevel <- Some top
            // Android back button: close whatever is open inside the app first
            top.BackRequested.Add(fun args ->
                if Views.canGoBack then
                    args.Handled <- true
                    dispatch |> Option.iter (fun d -> d State.BackPressed))
            // keyboard: space play/pause, arrows 15 s, S study, Escape back (not while typing in a text
            // box); in a review, space or Enter shows the answer (then answers Good) and 1 to 4 answer or pick an option;
            // in the tutor's turn, 1 to 4 pick an option and Enter pauses or goes on
            top.AddHandler(
                Input.InputElement.KeyDownEvent,
                (fun _ (e: Input.KeyEventArgs) ->
                    let typing = e.Source :? TextBox
                    let msg =
                        match e.Key with
                        | Input.Key.Space | Input.Key.Enter when Views.reviewing && not typing -> Some State.ReviewNext
                        | Input.Key.D1 | Input.Key.NumPad1 when Views.reviewing -> Some(State.ReviewKey 1)
                        | Input.Key.D2 | Input.Key.NumPad2 when Views.reviewing -> Some(State.ReviewKey 2)
                        | Input.Key.D3 | Input.Key.NumPad3 when Views.reviewing -> Some(State.ReviewKey 3)
                        | Input.Key.D4 | Input.Key.NumPad4 when Views.reviewing -> Some(State.ReviewKey 4)
                        | _ when Views.reviewing && e.Key <> Input.Key.Escape -> None
                        | Input.Key.D1 | Input.Key.NumPad1 when Views.studying && not typing -> Some(State.StudyKey 1)
                        | Input.Key.D2 | Input.Key.NumPad2 when Views.studying && not typing -> Some(State.StudyKey 2)
                        | Input.Key.D3 | Input.Key.NumPad3 when Views.studying && not typing -> Some(State.StudyKey 3)
                        | Input.Key.D4 | Input.Key.NumPad4 when Views.studying && not typing -> Some(State.StudyKey 4)
                        | Input.Key.Enter when Views.studying && not typing -> Some State.TogglePlay
                        | Input.Key.Space when not typing -> Some State.TogglePlay
                        | Input.Key.Left when not typing -> Some State.Back15
                        | Input.Key.Right when not typing -> Some State.Forward15
                        | Input.Key.A when not typing && e.KeyModifiers = Input.KeyModifiers.None -> Some(State.OpenHelp None)
                        | Input.Key.S when not typing && e.KeyModifiers = Input.KeyModifiers.None && Views.inReader -> Some State.OpenStudy
                        | Input.Key.Escape when Views.canGoBack -> Some State.BackPressed
                        | _ -> None
                    match msg, dispatch with
                    | Some m, Some d ->
                        e.Handled <- true
                        d m
                    | _ -> ()),
                Interactivity.RoutingStrategies.Tunnel)
            // Avalonia keeps the content clear of the status and navigation bars itself, but not of the on-screen
            // keyboard: the part of the app it covers becomes padding, so what is typed (at the bottom) stays in view
            match top.InputPane with
            | null -> ()
            | pane ->
                pane.StateChanged.Add(fun e ->
                    let covered =
                        if e.NewState <> Platform.InputPaneState.Open || e.EndRect.Height <= 0.0 then 0.0
                        else
                            let bottom = this.TranslatePoint(Point(0.0, this.Bounds.Height), top)
                            if bottom.HasValue then max 0.0 (bottom.Value.Y - e.EndRect.Top) else e.EndRect.Height
                    this.Padding <- Thickness(0.0, 0.0, 0.0, covered))

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? ISingleViewApplicationLifetime as single -> single.MainView <- MainView()
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- HostWindow(Content = MainView(), Title = "Paper Reader", Width = 480.0, Height = 860.0, MinWidth = 360.0, MinHeight = 560.0)
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
