namespace Nao.Assistant

open System
open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout

/// Business component: the chat composer. A themed message box plus a toolbar (attach
/// + agent picker on the left, send on the right) and an optional attachment chip,
/// styled consistently with the rest of the app's inputs and buttons.
module ChatInput =

    /// Everything the composer needs: current input, the optional attached file, the
    /// agent choices, and the callbacks for each user action.
    type Props =
        { /// Current text in the message box.
          Text: string
          /// Placeholder shown when the box is empty.
          Placeholder: string
          /// Whether the composer accepts input / sending right now.
          CanSend: bool
          /// Name of the file attached to the next message, if any.
          AttachedFileName: string option
          /// Agent names the user can pick from.
          Agents: string list
          /// The currently selected agent.
          SelectedAgent: string
          /// Whether the agent can still be changed (only before the first message).
          AgentSelectable: bool
          OnTextChanged: string -> unit
          OnAttach: unit -> unit
          OnClearAttachment: unit -> unit
          OnAgentSelected: string -> unit
          OnSend: unit -> unit }

    let private attachmentChip (name: string) (onClear: unit -> unit) : IView =
        Border.create [
            Border.background Theme.surfaceInset
            Border.cornerRadius 8.0
            Border.padding (8.0, 3.0)
            Border.child (
                StackPanel.create [
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 6.0
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text (sprintf "\U0001F4CE %s" name)
                            TextBlock.fontSize 11.0
                            TextBlock.verticalAlignment VerticalAlignment.Center
                            TextBlock.foreground Theme.textPrimary
                        ]
                        Components.iconButton "\u00D7" true onClear
                    ]
                ]
            )
        ]

    /// Render the composer.
    let render (props: Props) : IView =
        StackPanel.create [
            StackPanel.orientation Orientation.Vertical
            StackPanel.spacing 8.0
            StackPanel.margin (12.0, 8.0, 12.0, 12.0)
            StackPanel.children [
                match props.AttachedFileName with
                | Some name ->
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.children [ attachmentChip name props.OnClearAttachment ]
                    ]
                | None -> ()

                Components.multilineInput
                    props.Text props.Placeholder 44.0 props.CanSend props.OnTextChanged

                Grid.create [
                    Grid.columnDefinitions "*,Auto"
                    Grid.children [
                        StackPanel.create [
                            Grid.column 0
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 6.0
                            StackPanel.verticalAlignment VerticalAlignment.Center
                            StackPanel.children [
                                Components.iconButton "\U0001F4CE" true props.OnAttach
                                ComboBox.create [
                                    ComboBox.minWidth 160.0
                                    ComboBox.fontSize 12.0
                                    ComboBox.dataItems (
                                        if props.Agents.IsEmpty then [ props.SelectedAgent ] else props.Agents)
                                    ComboBox.selectedItem (box props.SelectedAgent)
                                    ComboBox.isEnabled props.AgentSelectable
                                    ComboBox.onSelectedItemChanged (fun item ->
                                        match item with
                                        | :? string as s when not (String.IsNullOrWhiteSpace s) ->
                                            props.OnAgentSelected s
                                        | _ -> ())
                                ]
                            ]
                        ]
                        StackPanel.create [
                            Grid.column 1
                            StackPanel.children [
                                Components.button
                                    "Send"
                                    (props.CanSend && not (String.IsNullOrWhiteSpace props.Text))
                                    props.OnSend
                            ]
                        ]
                    ]
                ]
            ]
        ]
