namespace Nao.Assistant

open System
open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Media

/// Business component: the execution trace behind a Nao answer — the orchestrator's
/// reasoning per round and each tool / sub-agent call (with its input and output), in
/// the order they happened. Lets the user see the whole process, not just the answer.
module ExecutionTrace =

    /// One step of the process Nao ran to answer a turn: a reasoning round, a tool
    /// invocation, or a sub-agent delegation.
    type Step =
        { Kind: string
          Name: string
          Args: string
          Result: string }

    let private labelFor (s: Step) =
        match s.Kind with
        | "reasoning" -> "Orchestrator"
        | "agent" -> sprintf "Agent: %s" s.Name
        | _ -> sprintf "Tool: %s" s.Name

    let private accentFor (s: Step) =
        match s.Kind with
        | "reasoning" -> Theme.traceReasoning
        | "agent" -> Theme.traceAgent
        | _ -> Theme.traceTool

    /// Render an ordered, read-only trace of the given steps inside a collapsible
    /// section. `expanded` controls the initial open state (default open).
    let renderExpandable (expanded: bool) (steps: Step list) : IView =
        Border.create [
            Border.margin (0.0, 6.0, 0.0, 0.0)
            Border.padding (8.0, 6.0)
            Border.cornerRadius 4.0
            Border.background Theme.surfaceInset
            Border.borderThickness 1.0
            Border.borderBrush Theme.borderSubtle
            Border.child (
                Expander.create [
                    Expander.isExpanded expanded
                    Expander.background Brushes.Transparent
                    Expander.borderThickness 0.0
                    Expander.padding (0.0, 0.0)
                    Expander.header (
                        TextBlock.create [
                            TextBlock.text (Localization.current ()).ExecutionTraceLabel
                            TextBlock.fontSize 10.0
                            TextBlock.fontWeight FontWeight.SemiBold
                            TextBlock.foreground Theme.textMuted
                        ]
                    )
                    Expander.content (
                        StackPanel.create [
                            StackPanel.spacing 6.0
                            StackPanel.margin (0.0, 6.0, 0.0, 0.0)
                            StackPanel.children [
                                for (i, s) in List.indexed steps do
                                    Border.create [
                                        Border.padding (6.0, 4.0)
                                        Border.cornerRadius 4.0
                                        Border.background Theme.surface
                                        Border.child (
                                            StackPanel.create [
                                                StackPanel.spacing 2.0
                                                StackPanel.children [
                                                    TextBlock.create [
                                                        TextBlock.text (sprintf "%d. %s" (i + 1) (labelFor s))
                                                        TextBlock.fontSize 11.0
                                                        TextBlock.fontWeight FontWeight.SemiBold
                                                        TextBlock.foreground (accentFor s)
                                                    ]
                                                    if not (String.IsNullOrWhiteSpace s.Args) then
                                                        SelectableTextBlock.create [
                                                            SelectableTextBlock.text (sprintf "\u2192 %s" s.Args)
                                                            SelectableTextBlock.fontSize 11.0
                                                            SelectableTextBlock.textWrapping TextWrapping.Wrap
                                                            SelectableTextBlock.foreground Theme.textSecondary
                                                        ]
                                                    if not (String.IsNullOrWhiteSpace s.Result) then
                                                        SelectableTextBlock.create [
                                                            SelectableTextBlock.text (sprintf "\u2190 %s" s.Result)
                                                            SelectableTextBlock.fontSize 11.0
                                                            SelectableTextBlock.textWrapping TextWrapping.Wrap
                                                            SelectableTextBlock.foreground Theme.textSecondary
                                                        ]
                                                ]
                                            ]
                                        )
                                    ]
                            ]
                        ]
                    )
                ]
            )
        ]

    /// Render an ordered, read-only trace of the given steps (collapsible, open by default).
    let render (steps: Step list) : IView =
        renderExpandable true steps

