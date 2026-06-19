namespace Nao.Assistant

open System
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Input
open Avalonia.Threading
open global.Elmish
open Nao.Assistant

/// Main shell — browser-like tabbed interface
module Shell =

    type ServerStartupState =
        | Starting
        | Started of string
        | Failed of string

    type ActiveView =
        | SessionTab of int
        | Settings

    type ShellState =
        { Sessions: SessionView.SessionState list
          ActiveView: ActiveView
          SettingsState: SettingsView.SettingsState
          ServerStartup: ServerStartupState
          Client: NaoClient option }

    type ShellMsg =
        | StartupSucceeded of NaoClient * string * SessionView.SessionState list
        | StartupFailed of string
        | RetryStartup
        | NewSession
        | SelectSession of int
        | CloseSession of int
        | OpenSettings
        | SessionMsg of int * SessionView.Msg
        | SettingsMsg of SettingsView.Msg

    let private startupCmd (settings: AppSettings) : Cmd<ShellMsg> =
        [ fun dispatch ->
            Task.Run<unit>(Func<Task<unit>>(fun () ->
                task {
                    try
                        let serverUrl = EmbeddedServer.start settings
                        let client = new NaoClient(serverUrl)
                        let! entries = client.ListSessionsAsync()
                        let restored =
                            if entries.Length > 0 then
                                entries
                                |> List.map (fun entry ->
                                    { SessionView.Id = entry.SessionId.Split('/').[1]
                                      SessionView.ServerSessionId = Some entry.SessionId
                                      SessionView.Title = entry.Title
                                      SessionView.Messages = []
                                      SessionView.Input = ""
                                      SessionView.Chat = SessionView.Idle
                                      SessionView.History = SessionView.NeedsLoad
                                      SessionView.Feedback = None } : SessionView.SessionState)
                            else
                                [ SessionView.createNew () ]

                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (StartupSucceeded (client, serverUrl, restored)))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (StartupFailed ex.Message))
                }))
            |> ignore ]

    let init () : ShellState * Cmd<ShellMsg> =
        let settings = AppSettingsStore.load ()
        { Sessions = []
          ActiveView = SessionTab 0
          SettingsState =
            { Settings = settings
              IsDirty = false
              StatusMessage = "" }
          ServerStartup = Starting
          Client = None },
        startupCmd settings

    let update (msg: ShellMsg) (model: ShellState) : ShellState * Cmd<ShellMsg> =
        match msg with
        | StartupSucceeded (client, serverUrl, sessions) ->
            let trigger =
                match sessions with
                | first :: _ when first.ServerSessionId.IsSome ->
                    Cmd.ofMsg (SessionMsg (0, SessionView.TriggerHistoryLoad))
                | _ -> Cmd.none
            { model with
                Sessions = sessions
                ActiveView = SessionTab 0
                ServerStartup = Started serverUrl
                Client = Some client },
            trigger

        | StartupFailed err ->
            { model with ServerStartup = Failed err }, Cmd.none

        | RetryStartup ->
            { model with ServerStartup = Starting }, startupCmd model.SettingsState.Settings

        | NewSession ->
            let session = SessionView.createNew ()
            let idx = model.Sessions.Length
            { model with
                Sessions = model.Sessions @ [ session ]
                ActiveView = SessionTab idx },
            Cmd.none

        | SelectSession i ->
            if i < 0 || i >= model.Sessions.Length then
                model, Cmd.none
            else
                let sessions =
                    model.Sessions
                    |> List.mapi (fun idx s ->
                        if idx = i then
                            { s with
                                History =
                                    if s.ServerSessionId.IsSome
                                    then SessionView.NeedsLoad
                                    else SessionView.Loaded }
                        else
                            s)
                let trigger =
                    if sessions.[i].ServerSessionId.IsSome then
                        Cmd.ofMsg (SessionMsg (i, SessionView.TriggerHistoryLoad))
                    else
                        Cmd.none
                { model with Sessions = sessions; ActiveView = SessionTab i }, trigger

        | CloseSession i ->
            if i < 0 || i >= model.Sessions.Length then
                model, Cmd.none
            else
                let updated = model.Sessions |> List.removeAt i
                let newActive =
                    match model.ActiveView with
                    | SessionTab idx when idx >= updated.Length -> SessionTab (max 0 (updated.Length - 1))
                    | SessionTab idx when idx > i -> SessionTab (idx - 1)
                    | other -> other
                { model with Sessions = updated; ActiveView = newActive }, Cmd.none

        | OpenSettings ->
            { model with ActiveView = Settings }, Cmd.none

        | SessionMsg (i, sessionMsg) ->
            match model.Client with
            | Some client when i >= 0 && i < model.Sessions.Length ->
                let updated, cmd = SessionView.update client sessionMsg model.Sessions.[i]
                let sessions =
                    model.Sessions
                    |> List.mapi (fun idx s -> if idx = i then updated else s)
                { model with Sessions = sessions }, Cmd.map (fun m -> SessionMsg (i, m)) cmd
            | _ ->
                model, Cmd.none

        | SettingsMsg settingsMsg ->
            match settingsMsg with
            | SettingsView.SettingsChanged newSettings ->
                { model with
                    SettingsState =
                        { model.SettingsState with
                            Settings = newSettings
                            IsDirty = true } },
                Cmd.none
            | SettingsView.Save ->
                AppSettingsStore.save model.SettingsState.Settings
                { model with
                    SettingsState =
                        { model.SettingsState with
                            IsDirty = false
                            StatusMessage = "Settings saved." } },
                Cmd.none
            | SettingsView.Close ->
                { model with ActiveView = SessionTab 0 }, Cmd.none

    let private tabBar (model: ShellState) (dispatch: ShellMsg -> unit) : Avalonia.FuncUI.Types.IView =
        Border.create [
            Border.dock Dock.Top
            Border.padding (8.0, 4.0)
            Border.background (SolidColorBrush(Color.Parse("#18181B")))
            Border.borderThickness (0.0, 0.0, 0.0, 1.0)
            Border.borderBrush (SolidColorBrush(Color.Parse("#3F3F46")))
            Border.child (
                DockPanel.create [
                    DockPanel.lastChildFill true
                    DockPanel.children [
                        // Right side: settings + new tab button
                        StackPanel.create [
                            StackPanel.dock Dock.Right
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 4.0
                            StackPanel.children [
                                Button.create [
                                    Button.content "+"
                                    Button.fontSize 16.0
                                    Button.padding (8.0, 2.0)
                                    Button.background Brushes.Transparent
                                    Button.foreground Brushes.White
                                    Button.onClick (fun _ -> dispatch NewSession)
                                ]
                                Button.create [
                                    Button.content "⚙"
                                    Button.fontSize 16.0
                                    Button.padding (8.0, 2.0)
                                    Button.background (
                                        match model.ActiveView with
                                        | Settings -> SolidColorBrush(Color.Parse("#3F3F46")) :> IBrush
                                        | _ -> Brushes.Transparent :> IBrush)
                                    Button.foreground Brushes.White
                                    Button.onClick (fun _ ->
                                        dispatch OpenSettings)
                                ]
                            ]
                        ]

                        // Tabs
                        ScrollViewer.create [
                            ScrollViewer.horizontalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                            ScrollViewer.verticalScrollBarVisibility Primitives.ScrollBarVisibility.Disabled
                            ScrollViewer.content (
                                StackPanel.create [
                                    StackPanel.orientation Orientation.Horizontal
                                    StackPanel.spacing 2.0
                                    StackPanel.children [
                                        for i, session in model.Sessions |> List.indexed do
                                            let isActive =
                                                match model.ActiveView with
                                                | SessionTab idx -> idx = i
                                                | _ -> false

                                            Border.create [
                                                Border.padding (10.0, 4.0)
                                                Border.cornerRadius (6.0, 6.0, 0.0, 0.0)
                                                Border.background (
                                                    if isActive
                                                    then SolidColorBrush(Color.Parse("#27272A")) :> IBrush
                                                    else Brushes.Transparent :> IBrush)
                                                Border.child (
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 6.0
                                                        StackPanel.children [
                                                            Button.create [
                                                                Button.content session.Title
                                                                Button.background Brushes.Transparent
                                                                Button.foreground (
                                                                    if isActive then Brushes.White :> IBrush
                                                                    else SolidColorBrush(Color.Parse("#A1A1AA")) :> IBrush)
                                                                Button.padding (4.0, 2.0)
                                                                Button.onClick (fun _ ->
                                                                    dispatch (SelectSession i))
                                                            ]
                                                            if model.Sessions.Length > 1 then
                                                                Button.create [
                                                                    Button.content "×"
                                                                    Button.fontSize 12.0
                                                                    Button.padding (2.0, 0.0)
                                                                    Button.background Brushes.Transparent
                                                                    Button.foreground (SolidColorBrush(Color.Parse("#71717A")))
                                                                    Button.onClick (fun _ ->
                                                                        dispatch (CloseSession i))
                                                                ]
                                                        ]
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

    let view (model: ShellState) (dispatch: ShellMsg -> unit) : Avalonia.FuncUI.Types.IView =
        match model.ServerStartup with
            | Starting ->
                Grid.create [
                    Grid.background (SolidColorBrush(Color.Parse("#09090B")))
                    Grid.children [
                        StackPanel.create [
                            StackPanel.horizontalAlignment HorizontalAlignment.Center
                            StackPanel.verticalAlignment VerticalAlignment.Center
                            StackPanel.spacing 12.0
                            StackPanel.width 320.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text "Starting local server..."
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                    TextBlock.foreground Brushes.White
                                    TextBlock.fontSize 16.0
                                ]
                                ProgressBar.create [
                                    ProgressBar.height 8.0
                                    ProgressBar.isIndeterminate true
                                ]
                                TextBlock.create [
                                    TextBlock.text "Preparing sessions and runtime"
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                    TextBlock.foreground (SolidColorBrush(Color.Parse("#A1A1AA")))
                                ]
                            ]
                        ]
                    ]
                ]
            | Failed err ->
                Grid.create [
                    Grid.background (SolidColorBrush(Color.Parse("#09090B")))
                    Grid.children [
                        StackPanel.create [
                            StackPanel.horizontalAlignment HorizontalAlignment.Center
                            StackPanel.verticalAlignment VerticalAlignment.Center
                            StackPanel.spacing 12.0
                            StackPanel.width 520.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text "Server failed to start"
                                    TextBlock.foreground Brushes.White
                                    TextBlock.fontSize 18.0
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                ]
                                SelectableTextBlock.create [
                                    SelectableTextBlock.text err
                                    SelectableTextBlock.foreground (SolidColorBrush(Color.Parse("#FCA5A5")))
                                    SelectableTextBlock.textWrapping TextWrapping.Wrap
                                ]
                                Button.create [
                                    Button.content "Retry"
                                    Button.horizontalAlignment HorizontalAlignment.Center
                                    Button.onClick (fun _ ->
                                        dispatch RetryStartup)
                                ]
                            ]
                        ]
                    ]
                ]
            | Started _ ->
                DockPanel.create [
                    DockPanel.lastChildFill true
                    DockPanel.background (SolidColorBrush(Color.Parse("#09090B")))
                    DockPanel.children [
                        // Tab bar
                        tabBar model dispatch

                        // Content area
                        match model.ActiveView with
                        | Settings ->
                            SettingsView.view (SettingsMsg >> dispatch) model.SettingsState

                        | SessionTab idx ->
                            if idx >= 0 && idx < model.Sessions.Length then
                                SessionView.view (fun m -> dispatch (SessionMsg (idx, m))) model.Sessions.[idx]
                            else
                                TextBlock.create [
                                    TextBlock.text "No session selected"
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                ]
                    ]
                ]
