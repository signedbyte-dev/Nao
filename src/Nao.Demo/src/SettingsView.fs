namespace Nao.Demo

open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media

/// Settings view — global app + orchestrator configuration
module SettingsView =

    type SettingsState =
        { Settings: AppSettings
          IsDirty: bool
          StatusMessage: string }

    type Msg =
        | SettingsChanged of AppSettings
        | Save
        | Close

    let view (dispatch: Msg -> unit) (model: SettingsState) : Avalonia.FuncUI.Types.IView =
        let s = model.Settings

        ScrollViewer.create [
            ScrollViewer.padding (20.0, 16.0)
            ScrollViewer.content (
                StackPanel.create [
                    StackPanel.orientation Orientation.Vertical
                    StackPanel.spacing 16.0
                    StackPanel.maxWidth 600.0
                    StackPanel.children [
                        // Header
                        DockPanel.create [
                            DockPanel.children [
                                Button.create [
                                    Button.dock Dock.Right
                                    Button.content "Close"
                                    Button.onClick (fun _ -> dispatch Close)
                                ]
                                TextBlock.create [
                                    TextBlock.text "Settings"
                                    TextBlock.fontSize 22.0
                                    TextBlock.fontWeight FontWeight.Bold
                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                ]
                            ]
                        ]

                        // Provider section
                        Border.create [
                            Border.padding (12.0, 10.0)
                            Border.cornerRadius 8.0
                            Border.background (SolidColorBrush(Color.Parse("#1E1E2E")))
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 8.0
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text "LLM Provider"
                                            TextBlock.fontSize 16.0
                                            TextBlock.fontWeight FontWeight.SemiBold
                                        ]
                                        // Provider type
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text "Type:"
                                                    TextBlock.width 100.0
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                ]
                                                TextBox.create [
                                                    TextBox.text s.Provider.ProviderType
                                                    TextBox.width 200.0
                                                    TextBox.onTextChanged (fun v ->
                                                        dispatch (SettingsChanged { s with Provider = { s.Provider with ProviderType = v } }))
                                                ]
                                            ]
                                        ]
                                        // Endpoint
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text "Endpoint:"
                                                    TextBlock.width 100.0
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                ]
                                                TextBox.create [
                                                    TextBox.text s.Provider.Endpoint
                                                    TextBox.width 300.0
                                                    TextBox.onTextChanged (fun v ->
                                                        dispatch (SettingsChanged { s with Provider = { s.Provider with Endpoint = v } }))
                                                ]
                                            ]
                                        ]
                                        // Model
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text "Model:"
                                                    TextBlock.width 100.0
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                ]
                                                TextBox.create [
                                                    TextBox.text s.Provider.Model
                                                    TextBox.width 200.0
                                                    TextBox.onTextChanged (fun v ->
                                                        dispatch (SettingsChanged { s with Provider = { s.Provider with Model = v } }))
                                                ]
                                            ]
                                        ]
                                    ]
                                ]
                            )
                        ]

                        // Orchestrator section
                        Border.create [
                            Border.padding (12.0, 10.0)
                            Border.cornerRadius 8.0
                            Border.background (SolidColorBrush(Color.Parse("#1E1E2E")))
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 8.0
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text "Orchestrator"
                                            TextBlock.fontSize 16.0
                                            TextBlock.fontWeight FontWeight.SemiBold
                                        ]
                                        // Max rounds
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text "Max Rounds:"
                                                    TextBlock.width 120.0
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                ]
                                                NumericUpDown.create [
                                                    NumericUpDown.value (decimal s.Orchestrator.MaxRounds)
                                                    NumericUpDown.minimum 1M
                                                    NumericUpDown.maximum 50M
                                                    NumericUpDown.increment 1M
                                                    NumericUpDown.width 100.0
                                                    NumericUpDown.onValueChanged (fun v ->
                                                        if v.HasValue then
                                                            dispatch (SettingsChanged { s with Orchestrator = { s.Orchestrator with MaxRounds = int v.Value } }))
                                                ]
                                            ]
                                        ]
                                        // Temperature
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text "Temperature:"
                                                    TextBlock.width 120.0
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                ]
                                                NumericUpDown.create [
                                                    NumericUpDown.value (decimal s.Orchestrator.Temperature)
                                                    NumericUpDown.minimum 0M
                                                    NumericUpDown.maximum 2M
                                                    NumericUpDown.increment 0.1M
                                                    NumericUpDown.width 100.0
                                                    NumericUpDown.onValueChanged (fun v ->
                                                        if v.HasValue then
                                                            dispatch (SettingsChanged { s with Orchestrator = { s.Orchestrator with Temperature = float v.Value } }))
                                                ]
                                            ]
                                        ]
                                        // Window strategy
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text "Window:"
                                                    TextBlock.width 120.0
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                ]
                                                ComboBox.create [
                                                    ComboBox.dataItems [ "LastN"; "TokenBudget"; "SummarizeAfter" ]
                                                    ComboBox.selectedItem (box s.Orchestrator.WindowStrategy)
                                                    ComboBox.width 160.0
                                                    ComboBox.onSelectedItemChanged (fun item ->
                                                        match item with
                                                        | :? string as v ->
                                                            dispatch (SettingsChanged { s with Orchestrator = { s.Orchestrator with WindowStrategy = v } })
                                                        | _ -> ())
                                                ]
                                                NumericUpDown.create [
                                                    NumericUpDown.value (decimal s.Orchestrator.WindowSize)
                                                    NumericUpDown.minimum 1M
                                                    NumericUpDown.maximum 100M
                                                    NumericUpDown.increment 5M
                                                    NumericUpDown.width 100.0
                                                    NumericUpDown.onValueChanged (fun v ->
                                                        if v.HasValue then
                                                            dispatch (SettingsChanged { s with Orchestrator = { s.Orchestrator with WindowSize = int v.Value } }))
                                                ]
                                            ]
                                        ]
                                        // System prompt
                                        TextBlock.create [
                                            TextBlock.text "System Prompt:"
                                            TextBlock.margin (0.0, 4.0, 0.0, 0.0)
                                        ]
                                        TextBox.create [
                                            TextBox.text s.Orchestrator.SystemPrompt
                                            TextBox.acceptsReturn true
                                            TextBox.minHeight 80.0
                                            TextBox.textWrapping TextWrapping.Wrap
                                            TextBox.onTextChanged (fun v ->
                                                dispatch (SettingsChanged { s with Orchestrator = { s.Orchestrator with SystemPrompt = v } }))
                                        ]
                                    ]
                                ]
                            )
                        ]

                        // Workspace section
                        Border.create [
                            Border.padding (12.0, 10.0)
                            Border.cornerRadius 8.0
                            Border.background (SolidColorBrush(Color.Parse("#1E1E2E")))
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 8.0
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text "Workspace"
                                            TextBlock.fontSize 16.0
                                            TextBlock.fontWeight FontWeight.SemiBold
                                        ]
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children [
                                                TextBlock.create [
                                                    TextBlock.text "Path:"
                                                    TextBlock.width 100.0
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                ]
                                                TextBox.create [
                                                    TextBox.text s.WorkspacePath
                                                    TextBox.width 350.0
                                                    TextBox.watermark "Path to .nao workspace folder"
                                                    TextBox.onTextChanged (fun v ->
                                                        dispatch (SettingsChanged { s with WorkspacePath = v }))
                                                ]
                                            ]
                                        ]
                                        TextBlock.create [
                                            TextBlock.text "Place an orchestrator.json in .nao/ folder to override orchestrator settings per workspace."
                                            TextBlock.fontSize 11.0
                                            TextBlock.foreground (SolidColorBrush(Color.Parse("#71717A")))
                                            TextBlock.textWrapping TextWrapping.Wrap
                                        ]
                                    ]
                                ]
                            )
                        ]

                        // Save button
                        DockPanel.create [
                            DockPanel.children [
                                Button.create [
                                    Button.dock Dock.Right
                                    Button.content "Save"
                                    Button.isEnabled model.IsDirty
                                    Button.onClick (fun _ -> dispatch Save)
                                ]
                                TextBlock.create [
                                    TextBlock.text model.StatusMessage
                                    TextBlock.foreground (SolidColorBrush(Color.Parse("#4ADE80")))
                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                ]
                            ]
                        ]
                    ]
                ]
            )
        ]
