namespace Nao.Assistant

open System
open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media

/// Business component: a single chat message bubble. Owns the bubble chrome (border,
/// alignment, role label, attachment chips and the message body) and exposes a footer
/// slot for role-specific controls (e.g. an assistant answer's details/trace and
/// feedback actions), which the caller supplies so interaction stays in the view.
module MessageBubble =

    /// Everything needed to render one message bubble.
    type Props =
        { /// Display name of the speaker ("You" / "Nao").
          Role: string
          /// True for the user's own messages (right-aligned, raised surface).
          IsUser: bool
          /// The message body text (may be empty for attachment-only messages).
          Content: string
          /// File names attached to this message, rendered as chips.
          Attachments: string list
          /// Views shown ABOVE the body (e.g. the execution trace / process steps that
          /// led to the answer), so the process reads before the final response.
          Header: IView list
          /// Extra views shown beneath the body (feedback actions, ...).
          Footer: IView list }

    let private attachmentChip (name: string) : IView =
        Border.create [
            Border.margin (0.0, 4.0, 0.0, 0.0)
            Border.padding (8.0, 4.0)
            Border.cornerRadius 6.0
            Border.background Theme.surfaceInset
            Border.borderThickness 1.0
            Border.borderBrush Theme.border
            Border.child (
                StackPanel.create [
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 6.0
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text "\U0001F4CE"
                            TextBlock.fontSize 13.0
                            TextBlock.verticalAlignment VerticalAlignment.Center
                        ]
                        TextBlock.create [
                            TextBlock.text name
                            TextBlock.fontSize 12.0
                            TextBlock.verticalAlignment VerticalAlignment.Center
                            TextBlock.foreground Theme.textSecondary
                        ]
                    ]
                ]
            )
        ]

    /// Render the message bubble. The bubble scrolls itself into view when loaded so
    /// the conversation auto-advances to the latest message.
    let render (props: Props) : IView =
        Border.create [
            Control.onLoaded (fun args ->
                match args.Source with
                | :? Control as c -> c.BringIntoView()
                | _ -> ())
            Border.padding (12.0, 10.0)
            Border.cornerRadius 12.0
            Border.borderThickness 1.0
            Border.borderBrush Theme.borderSubtle
            Border.background (if props.IsUser then Theme.surfaceRaised else Theme.surface)
            Border.horizontalAlignment (
                if props.IsUser then HorizontalAlignment.Right else HorizontalAlignment.Left)
            Border.maxWidth 600.0
            Border.child (
                StackPanel.create [
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text props.Role
                            TextBlock.fontSize 11.0
                            TextBlock.foreground Theme.textSecondary
                        ]
                        yield! props.Header
                        if not (String.IsNullOrWhiteSpace props.Content) then
                            SelectableTextBlock.create [
                                SelectableTextBlock.text props.Content
                                SelectableTextBlock.textWrapping TextWrapping.Wrap
                                SelectableTextBlock.foreground Theme.textPrimary
                            ]
                        for att in props.Attachments do
                            attachmentChip att
                        yield! props.Footer
                    ]
                ]
            )
        ]
