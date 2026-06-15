namespace Nao.Demo

open System
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Input
open Nao.Demo

/// Main shell — browser-like tabbed interface
module Shell =

    type ActiveView =
        | SessionTab of int
        | Settings

    type ShellState =
        { Sessions: SessionView.SessionState list
          ActiveView: ActiveView
          SettingsState: SettingsView.SettingsState }

    let private init () =
        let settings = AppSettingsStore.load ()
        { Sessions = [ SessionView.createNew () ]
          ActiveView = SessionTab 0
          SettingsState =
            { Settings = settings
              IsDirty = false
              StatusMessage = "" } }

    let private tabBar (state: IWritable<ShellState>) =
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
                                    Button.onClick (fun _ ->
                                        let newSession = SessionView.createNew ()
                                        let idx = state.Current.Sessions.Length
                                        state.Set { state.Current with
                                                        Sessions = state.Current.Sessions @ [ newSession ]
                                                        ActiveView = SessionTab idx })
                                ]
                                Button.create [
                                    Button.content "⚙"
                                    Button.fontSize 16.0
                                    Button.padding (8.0, 2.0)
                                    Button.background (
                                        match state.Current.ActiveView with
                                        | Settings -> SolidColorBrush(Color.Parse("#3F3F46")) :> IBrush
                                        | _ -> Brushes.Transparent :> IBrush)
                                    Button.foreground Brushes.White
                                    Button.onClick (fun _ ->
                                        state.Set { state.Current with ActiveView = Settings })
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
                                        for i, session in state.Current.Sessions |> List.indexed do
                                            let isActive =
                                                match state.Current.ActiveView with
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
                                                                    state.Set { state.Current with ActiveView = SessionTab i })
                                                            ]
                                                            if state.Current.Sessions.Length > 1 then
                                                                Button.create [
                                                                    Button.content "×"
                                                                    Button.fontSize 12.0
                                                                    Button.padding (2.0, 0.0)
                                                                    Button.background Brushes.Transparent
                                                                    Button.foreground (SolidColorBrush(Color.Parse("#71717A")))
                                                                    Button.onClick (fun _ ->
                                                                        let updated = state.Current.Sessions |> List.removeAt i
                                                                        let newActive =
                                                                            match state.Current.ActiveView with
                                                                            | SessionTab idx when idx >= updated.Length -> SessionTab (updated.Length - 1)
                                                                            | SessionTab idx when idx > i -> SessionTab (idx - 1)
                                                                            | other -> other
                                                                        state.Set { state.Current with Sessions = updated; ActiveView = newActive })
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

    let view () =
        Component(fun ctx ->
            let state = ctx.useState (init ())
            let settingsState = ctx.useState state.Current.SettingsState
            let activeSessionIdx =
                match state.Current.ActiveView with
                | SessionTab idx -> idx
                | _ -> 0
            let sessionState =
                let sessions = state.Current.Sessions
                let idx = if activeSessionIdx >= 0 && activeSessionIdx < sessions.Length then activeSessionIdx else 0
                ctx.useState sessions.[idx]

            let serverUrl =
                let endpoint = settingsState.Current.Settings.Provider.Endpoint
                if String.IsNullOrWhiteSpace(endpoint) then "http://localhost:5000"
                else
                    // Use server URL from env or default
                    match Environment.GetEnvironmentVariable("NAO_SERVER") with
                    | null | "" -> "http://localhost:5000"
                    | url -> url
            let client = new NaoClient(serverUrl)

            DockPanel.create [
                DockPanel.lastChildFill true
                DockPanel.background (SolidColorBrush(Color.Parse("#09090B")))
                DockPanel.children [
                    // Tab bar
                    tabBar state

                    // Content area
                    match state.Current.ActiveView with
                    | Settings ->
                        SettingsView.view settingsState (fun () ->
                            state.Set { state.Current with
                                            ActiveView = SessionTab 0
                                            SettingsState = settingsState.Current })

                    | SessionTab idx ->
                        let sessions = state.Current.Sessions
                        if idx >= 0 && idx < sessions.Length then
                            SessionView.view client sessionState
                        else
                            TextBlock.create [
                                TextBlock.text "No session selected"
                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                ]
            ]
        )
