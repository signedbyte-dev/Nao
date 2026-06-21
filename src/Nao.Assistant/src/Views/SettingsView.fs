namespace Nao.Assistant

open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
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

    /// Header row: title on the left, Close button on the right.
    let private header (dispatch: Msg -> unit) : IView =
        let t = Localization.current ()
        DockPanel.create [
            DockPanel.children [
                Button.create [
                    Button.dock Dock.Right
                    Button.content t.Close
                    Button.onClick (fun _ -> dispatch Close)
                ]
                TextBlock.create [
                    TextBlock.text t.SettingsTitle
                    TextBlock.fontSize 22.0
                    TextBlock.fontWeight FontWeight.Bold
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ]
            ]
        ]

    /// Appearance: theme (dark/light) and UI language. Applied live by the Shell when the
    /// settings change, and persisted on Save.
    let private appearanceSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        let t = Localization.current ()
        let change f = dispatch (SettingsChanged (f s))
        FormControls.section t.Appearance [
            FormControls.row t.Theme 120.0 [
                ComboBox.create [
                    ComboBox.dataItems [ t.ThemeDark; t.ThemeLight ]
                    ComboBox.selectedItem (box (if Theme.parse s.Theme = Theme.Light then t.ThemeLight else t.ThemeDark))
                    ComboBox.width 160.0
                    ComboBox.onSelectedItemChanged (fun item ->
                        match item with
                        | :? string as v ->
                            let theme = if v = t.ThemeLight then "Light" else "Dark"
                            change (fun s -> { s with Theme = theme })
                        | _ -> ())
                ]
            ]
            FormControls.row t.Language 120.0 [
                ComboBox.create [
                    ComboBox.dataItems (Localization.all |> List.map Localization.displayName)
                    ComboBox.selectedItem (box (Localization.displayName (Localization.parse s.Language)))
                    ComboBox.width 160.0
                    ComboBox.onSelectedItemChanged (fun item ->
                        match item with
                        | :? string as name ->
                            let lang =
                                Localization.all
                                |> List.tryFind (fun l -> Localization.displayName l = name)
                                |> Option.defaultValue Localization.English
                            change (fun s -> { s with Language = Localization.code lang })
                        | _ -> ())
                ]
            ]
        ]

    let private providerSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        let change f = dispatch (SettingsChanged (f s))
        FormControls.section "LLM Provider" [
            FormControls.textRow "Type:" 100.0 s.Provider.ProviderType 200.0 (fun v ->
                change (fun s -> { s with Provider = { s.Provider with ProviderType = v } }))
            FormControls.textRow "Endpoint:" 100.0 s.Provider.Endpoint 300.0 (fun v ->
                change (fun s -> { s with Provider = { s.Provider with Endpoint = v } }))
            FormControls.textRow "Model:" 100.0 s.Provider.Model 200.0 (fun v ->
                change (fun s -> { s with Provider = { s.Provider with Model = v } }))
        ]

    let private orchestratorSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        let change f = dispatch (SettingsChanged (f s))
        FormControls.section "Orchestrator" [
            FormControls.row "Max Rounds:" 120.0 [
                NumericUpDown.create [
                    NumericUpDown.value (decimal s.Orchestrator.MaxRounds)
                    NumericUpDown.minimum 1M
                    NumericUpDown.maximum 50M
                    NumericUpDown.increment 1M
                    NumericUpDown.width 100.0
                    NumericUpDown.onValueChanged (fun v ->
                        if v.HasValue then
                            change (fun s -> { s with Orchestrator = { s.Orchestrator with MaxRounds = int v.Value } }))
                ]
            ]
            FormControls.row "Temperature:" 120.0 [
                NumericUpDown.create [
                    NumericUpDown.value (decimal s.Orchestrator.Temperature)
                    NumericUpDown.minimum 0M
                    NumericUpDown.maximum 2M
                    NumericUpDown.increment 0.1M
                    NumericUpDown.width 100.0
                    NumericUpDown.onValueChanged (fun v ->
                        if v.HasValue then
                            change (fun s -> { s with Orchestrator = { s.Orchestrator with Temperature = float v.Value } }))
                ]
            ]
            FormControls.row "Window:" 120.0 [
                ComboBox.create [
                    ComboBox.dataItems [ "LastN"; "TokenBudget"; "SummarizeAfter" ]
                    ComboBox.selectedItem (box s.Orchestrator.WindowStrategy)
                    ComboBox.width 160.0
                    ComboBox.onSelectedItemChanged (fun item ->
                        match item with
                        | :? string as v ->
                            change (fun s -> { s with Orchestrator = { s.Orchestrator with WindowStrategy = v } })
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
                            change (fun s -> { s with Orchestrator = { s.Orchestrator with WindowSize = int v.Value } }))
                ]
            ]
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
                    change (fun s -> { s with Orchestrator = { s.Orchestrator with SystemPrompt = v } }))
            ]
        ]

    let private workspaceSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        FormControls.section "Workspace" [
            FormControls.row "Path:" 100.0 [
                TextBox.create [
                    TextBox.text s.WorkspacePath
                    TextBox.width 350.0
                    TextBox.watermark "Path to .nao workspace folder"
                    TextBox.onTextChanged (fun v ->
                        dispatch (SettingsChanged { s with WorkspacePath = v }))
                ]
            ]
            FormControls.hint
                "Place an orchestrator.json in .nao/ folder to override orchestrator settings per workspace."
        ]

    /// Footer row: status message on the left, Save button on the right.
    let private footer (dispatch: Msg -> unit) (model: SettingsState) : IView =
        DockPanel.create [
            DockPanel.children [
                Button.create [
                    Button.dock Dock.Right
                    Button.content (Localization.current ()).Save
                    Button.isEnabled model.IsDirty
                    Button.onClick (fun _ -> dispatch Save)
                ]
                TextBlock.create [
                    TextBlock.text model.StatusMessage
                    TextBlock.foreground Theme.success
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ]
            ]
        ]

    let view (dispatch: Msg -> unit) (model: SettingsState) : IView =
        let s = model.Settings
        ScrollViewer.create [
            ScrollViewer.content (
                StackPanel.create [
                    StackPanel.orientation Orientation.Vertical
                    StackPanel.spacing 16.0
                    StackPanel.maxWidth 600.0
                    // Padding lives on the content (not the ScrollViewer) so it is part of
                    // the scrollable extent — otherwise the bottom item is clipped at the end.
                    StackPanel.margin (20.0, 16.0)
                    StackPanel.children [
                        header dispatch
                        appearanceSection dispatch s
                        providerSection dispatch s
                        orchestratorSection dispatch s
                        workspaceSection dispatch s
                        footer dispatch model
                    ]
                ]
            )
        ]
