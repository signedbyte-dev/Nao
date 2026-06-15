namespace Nao.Demo

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Controls
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent

type MainWindow() as this =
    inherit HostWindow()
    do
        this.Title <- "Nao Desktop"
        this.Width <- 1000.0
        this.Height <- 700.0
        this.MinWidth <- 600.0
        this.MinHeight <- 400.0
        this.Content <- Shell.view ()

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow()
            desktop.ShutdownRequested.Add(fun _ ->
                EmbeddedServer.stop ())
        | _ -> ()
        base.OnFrameworkInitializationCompleted()

module Program =

    [<EntryPoint>]
    let main argv =
        // Start the embedded server before launching UI
        let settings = AppSettingsStore.load ()
        let _serverUrl = EmbeddedServer.start settings

        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(argv)
