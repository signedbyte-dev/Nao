namespace Nao.Assistant

open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout

/// Business component: the action row shown beneath an assistant answer — the
/// 👍 / 👎 feedback controls (or a thank-you once feedback was given). The execution
/// trace is no longer hidden behind a "Details" toggle; it is rendered inline above the
/// answer by the caller. Returns the views to drop into a message bubble's footer slot,
/// keeping interaction in the caller via plain callbacks.
module MessageActions =

    /// Everything needed to render the footer actions for one assistant message.
    type Props =
        { /// Sentiment ("positive"/"negative") once feedback has been submitted.
          FeedbackGiven: string option
          /// Called with the chosen sentiment ("positive"/"negative").
          OnFeedback: string -> unit }

    let private feedbackGivenLabel (sentiment: string) : IView =
        TextBlock.create [
            TextBlock.text (
                if sentiment = "positive"
                then sprintf "\U0001F44D %s" (Localization.current ()).ThanksFeedback
                else sprintf "\U0001F44E %s" (Localization.current ()).ThanksFeedback)
            TextBlock.fontSize 11.0
            TextBlock.margin (0.0, 6.0, 0.0, 0.0)
            TextBlock.foreground Theme.textSecondary
        ]

    let private feedbackButtons (props: Props) : IView =
        StackPanel.create [
            StackPanel.orientation Orientation.Horizontal
            StackPanel.spacing 6.0
            StackPanel.margin (0.0, 6.0, 0.0, 0.0)
            StackPanel.children [
                Button.create [
                    Button.content "\U0001F44D"
                    Button.fontSize 12.0
                    Button.padding (8.0, 2.0)
                    Button.onClick (fun _ -> props.OnFeedback "positive")
                ]
                Button.create [
                    Button.content "\U0001F44E"
                    Button.fontSize 12.0
                    Button.padding (8.0, 2.0)
                    Button.onClick (fun _ -> props.OnFeedback "negative")
                ]
            ]
        ]

    /// Build the footer views for an assistant message (empty for user messages).
    let render (props: Props) : IView list =
        [ match props.FeedbackGiven with
          | Some sentiment -> feedbackGivenLabel sentiment
          | None -> feedbackButtons props ]
