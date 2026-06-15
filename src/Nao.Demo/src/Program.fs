namespace Nao.Demo

open System
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Themes.Fluent
open Avalonia.Threading

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

            // Wire tool confirmation handler to show a popup dialog
            EmbeddedServer.setConfirmationHandler (fun req ->
                Dispatcher.UIThread.Post(fun () ->
                    let dialog = Window()
                    dialog.Title <- "Confirm Tool Execution"
                    dialog.Width <- 420.0
                    dialog.Height <- 200.0
                    dialog.WindowStartupLocation <- WindowStartupLocation.CenterOwner
                    dialog.CanResize <- false

                    let panel = StackPanel()
                    panel.Margin <- Thickness(20.0)
                    panel.Spacing <- 12.0

                    let header = TextBlock()
                    header.Text <- sprintf "The agent wants to use tool: %s" req.ToolName
                    header.FontWeight <- FontWeight.Bold
                    header.FontSize <- 14.0
                    header.Foreground <- SolidColorBrush(Colors.White)

                    let detail = TextBlock()
                    detail.Text <- sprintf "Input: %s" (if req.Input.Length > 200 then req.Input.[..199] + "..." else req.Input)
                    detail.TextWrapping <- TextWrapping.Wrap
                    detail.Foreground <- SolidColorBrush(Color.Parse("#A1A1AA"))

                    let buttonPanel = StackPanel()
                    buttonPanel.Orientation <- Orientation.Horizontal
                    buttonPanel.HorizontalAlignment <- HorizontalAlignment.Right
                    buttonPanel.Spacing <- 8.0
                    buttonPanel.Margin <- Thickness(0.0, 12.0, 0.0, 0.0)

                    let allowBtn = Button()
                    allowBtn.Content <- "Allow"
                    allowBtn.Click.Add(fun _ ->
                        req.Completion.TrySetResult(true) |> ignore
                        dialog.Close())

                    let denyBtn = Button()
                    denyBtn.Content <- "Deny"
                    denyBtn.Click.Add(fun _ ->
                        req.Completion.TrySetResult(false) |> ignore
                        dialog.Close())

                    dialog.Closed.Add(fun _ ->
                        req.Completion.TrySetResult(false) |> ignore)

                    buttonPanel.Children.Add(denyBtn)
                    buttonPanel.Children.Add(allowBtn)
                    panel.Children.Add(header)
                    panel.Children.Add(detail)
                    panel.Children.Add(buttonPanel)
                    dialog.Content <- panel

                    dialog.ShowDialog(desktop.MainWindow) |> ignore
                ))
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
