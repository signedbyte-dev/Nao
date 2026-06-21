namespace Nao.Assistant

open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media

/// Reusable form-building blocks for settings-style panels: titled section cards and
/// labeled horizontal rows, so views can compose forms from small, consistent pieces.
[<RequireQualifiedAccess>]
module FormControls =

    /// A titled card grouping related settings.
    let section (title: string) (children: IView list) : IView =
        Border.create [
            Border.padding (12.0, 10.0)
            Border.cornerRadius 12.0
            Border.background Theme.surface
            Border.child (
                StackPanel.create [
                    StackPanel.spacing 8.0
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text title
                            TextBlock.fontSize 16.0
                            TextBlock.fontWeight FontWeight.SemiBold
                        ]
                        yield! children
                    ]
                ]
            )
        ]

    /// A horizontal row: a fixed-width label followed by one or more controls.
    let row (label: string) (labelWidth: float) (controls: IView list) : IView =
        StackPanel.create [
            StackPanel.orientation Orientation.Horizontal
            StackPanel.spacing 8.0
            StackPanel.children [
                TextBlock.create [
                    TextBlock.text label
                    TextBlock.width labelWidth
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ]
                yield! controls
            ]
        ]

    /// A labeled single-line text field row.
    let textRow (label: string) (labelWidth: float) (value: string) (width: float) (onChange: string -> unit) : IView =
        row label labelWidth [
            TextBox.create [
                TextBox.text value
                TextBox.width width
                TextBox.onTextChanged onChange
            ]
        ]

    /// A small muted helper/caption line.
    let hint (text: string) : IView =
        TextBlock.create [
            TextBlock.text text
            TextBlock.fontSize 11.0
            TextBlock.foreground Theme.textMuted
            TextBlock.textWrapping TextWrapping.Wrap
        ]
