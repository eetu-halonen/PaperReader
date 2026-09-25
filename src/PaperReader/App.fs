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

    do
        this.Background <- Media.SolidColorBrush(Media.Color.Parse Views.Palette.bg)
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
        if not (isNull top) then
            Services.topLevel <- Some top
            // Android back button: close whatever is open inside the app first
            top.BackRequested.Add(fun args ->
                if Views.canGoBack then
                    args.Handled <- true
                    dispatch |> Option.iter (fun d -> d State.BackPressed))
            // keep content clear of the status and navigation bars
            match top.InsetsManager with
            | null -> ()
            | insets ->
                this.Padding <- insets.SafeAreaPadding
                insets.SafeAreaChanged.Add(fun a -> this.Padding <- a.SafeAreaPadding)

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? ISingleViewApplicationLifetime as single -> single.MainView <- MainView()
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- HostWindow(Content = MainView(), Width = 420.0, Height = 860.0)
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
