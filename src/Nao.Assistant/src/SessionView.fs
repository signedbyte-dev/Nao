namespace Nao.Assistant

open System
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open global.Avalonia.FuncUI.Elmish.ElmishHook
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open global.Elmish
open Nao.Assistant

/// A single chat session tab view
module SessionView =

    type Message =
        { MessageId: Guid
          Role: string
          Content: string
          Timestamp: DateTime
          /// Sentiment ("positive"/"negative") once feedback has been submitted for this message.
          FeedbackGiven: string option }

    /// Lifecycle of the session's conversation history.
    type HistoryStatus =
        | NeedsLoad
        | Loading
        | Loaded

    /// Whether the session is currently awaiting a chat response.
    type ChatStatus =
        | Idle
        | Sending

    /// State of the optional feedback popup overlay for a single response.
    type FeedbackPrompt =
        { MessageId: Guid
          /// "positive" or "negative".
          Sentiment: string
          Comment: string
          Submitting: bool }

    type SessionState =
        { Id: string
          ServerSessionId: string option
          Title: string
          Messages: Message list
          Input: string
          Chat: ChatStatus
          History: HistoryStatus
          /// Active feedback popup, if the user is currently rating a response.
          Feedback: FeedbackPrompt option }

    let createNew () =
        { Id = Guid.NewGuid().ToString("N").[..7]
          ServerSessionId = None
          Title = "New Session"
          Messages = []
          Input = ""
          Chat = Idle
          History = Loaded
          Feedback = None }

    type Msg =
        | TriggerHistoryLoad
        | HistoryLoaded of Result<Message list, string>
        | InputChanged of string
        | SendPressed
        | SendCompleted of Result<string * string, string>
        | FeedbackRequested of messageId: Guid * sentiment: string
        | FeedbackCommentChanged of string
        | FeedbackCancelled
        | FeedbackSubmitPressed
        | FeedbackCompleted of Result<Guid * string, string>

    let private toUiMessage (m: MessageDto) : Message =
        let role =
            match (if isNull m.Role then "" else m.Role).ToLowerInvariant() with
            | "user" -> "You"
            | "assistant" -> "Nao"
            | r -> r
        { MessageId = Guid.NewGuid()
          Role = role
          Content = (if isNull m.Content then "" else m.Content)
          Timestamp = DateTime.Now
          FeedbackGiven = None }

    /// Build a fresh chat message with a unique id and no feedback yet.
    let private mkMessage (role: string) (content: string) : Message =
        { MessageId = Guid.NewGuid()
          Role = role
          Content = content
          Timestamp = DateTime.Now
          FeedbackGiven = None }

    let private cmdOfSub (sub: ('msg -> unit) -> unit) : Cmd<'msg> =
        [ sub ]

    let private loadHistoryCmd (client: NaoClient) (sessionId: string) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! msgs = client.LoadSessionHistoryAsync(sessionId)
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (HistoryLoaded (Ok (msgs |> List.map toUiMessage))))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (HistoryLoaded (Error ex.Message)))
                }
                :> Task)
            |> ignore)

    let private sendCmd (client: NaoClient) (model: SessionState) (userText: string) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! sessionId =
                            task {
                                match model.ServerSessionId with
                                | Some id -> return id
                                | None -> return! client.CreateSessionAsync(SessionStartRequest.Default)
                            }

                        let! response = client.ChatAsync(sessionId, userText)
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (SendCompleted (Ok (sessionId, response))))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (SendCompleted (Error ex.Message)))
                }
                :> Task)
            |> ignore)

    let private submitFeedbackCmd
        (client: NaoClient)
        (sessionId: string)
        (messageId: Guid)
        (sentiment: string)
        (comment: string)
        : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let comment = if String.IsNullOrWhiteSpace comment then None else Some comment
                        let! _ = client.SubmitFeedbackAsync(sessionId, sentiment, ?comment = comment)
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (FeedbackCompleted (Ok (messageId, sentiment))))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (FeedbackCompleted (Error ex.Message)))
                }
                :> Task)
            |> ignore)

    let update (client: NaoClient) (msg: Msg) (model: SessionState) : SessionState * Cmd<Msg> =
        match msg with
        | TriggerHistoryLoad ->
            match model.ServerSessionId with
            | Some sessionId when model.History = NeedsLoad ->
                { model with History = Loading }, loadHistoryCmd client sessionId
            | _ ->
                model, Cmd.none

        | HistoryLoaded (Ok restored) ->
            { model with
                Messages = restored
                History = Loaded },
            Cmd.none

        | HistoryLoaded (Error err) ->
            let loadError =
                mkMessage "System" (sprintf "Failed to load history: %s" err)
            { model with
                Messages = model.Messages @ [ loadError ]
                History = Loaded },
            Cmd.none

        | InputChanged text ->
            { model with Input = text }, Cmd.none

        | SendPressed ->
            let canSend =
                model.Chat = Idle
                && model.History = Loaded
                && not (String.IsNullOrWhiteSpace model.Input)

            if not canSend then
                model, Cmd.none
            else
                let text = model.Input
                let userMsg = mkMessage "You" text
                { model with
                    Messages = model.Messages @ [ userMsg ]
                    Input = ""
                    Chat = Sending },
                sendCmd client model text

        | SendCompleted (Ok (sessionId, response)) ->
            let replyMsg = mkMessage "Nao" response
            { model with
                ServerSessionId = Some sessionId
                Messages = model.Messages @ [ replyMsg ]
                Chat = Idle },
            Cmd.none

        | SendCompleted (Error err) ->
            let errorMsg = mkMessage "System" (sprintf "Error: %s" err)
            { model with Messages = model.Messages @ [ errorMsg ]; Chat = Idle }, Cmd.none

        | FeedbackRequested (messageId, sentiment) ->
            { model with
                Feedback = Some { MessageId = messageId; Sentiment = sentiment; Comment = ""; Submitting = false } },
            Cmd.none

        | FeedbackCommentChanged text ->
            match model.Feedback with
            | Some fb -> { model with Feedback = Some { fb with Comment = text } }, Cmd.none
            | None -> model, Cmd.none

        | FeedbackCancelled ->
            { model with Feedback = None }, Cmd.none

        | FeedbackSubmitPressed ->
            match model.Feedback, model.ServerSessionId with
            | Some fb, Some sessionId when not fb.Submitting ->
                { model with Feedback = Some { fb with Submitting = true } },
                submitFeedbackCmd client sessionId fb.MessageId fb.Sentiment fb.Comment
            | Some _, None ->
                // No server-side session yet (nothing to attach feedback to) — just close.
                { model with Feedback = None }, Cmd.none
            | _ -> model, Cmd.none

        | FeedbackCompleted (Ok (messageId, sentiment)) ->
            let messages =
                model.Messages
                |> List.map (fun m ->
                    if m.MessageId = messageId then { m with FeedbackGiven = Some sentiment } else m)
            { model with Messages = messages; Feedback = None }, Cmd.none

        | FeedbackCompleted (Error err) ->
            let errorMsg = mkMessage "System" (sprintf "Failed to send feedback: %s" err)
            { model with Messages = model.Messages @ [ errorMsg ]; Feedback = None }, Cmd.none

    /// Modal overlay letting the user attach an optional comment to their feedback.
    let private feedbackOverlay (dispatch: Msg -> unit) (fb: FeedbackPrompt) : Avalonia.FuncUI.Types.IView =
        let header =
            if fb.Sentiment = "positive" then "\U0001F44D Positive feedback" else "\U0001F44E Negative feedback"
        let prompt =
            if fb.Sentiment = "positive"
            then "What did you like about this response?"
            else "What could have been better?"
        Border.create [
            Border.background (SolidColorBrush(Color.Parse("#B3000000")))
            Border.child (
                Border.create [
                    Border.width 440.0
                    Border.padding (20.0, 18.0)
                    Border.cornerRadius 8.0
                    Border.horizontalAlignment HorizontalAlignment.Center
                    Border.verticalAlignment VerticalAlignment.Center
                    Border.background (SolidColorBrush(Color.Parse("#27272A")))
                    Border.borderThickness 1.0
                    Border.borderBrush (SolidColorBrush(Color.Parse("#3F3F46")))
                    Border.child (
                        StackPanel.create [
                            StackPanel.spacing 12.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text header
                                    TextBlock.fontSize 12.0
                                    TextBlock.foreground (SolidColorBrush(Color.Parse("#A1A1AA")))
                                ]
                                TextBlock.create [
                                    TextBlock.text prompt
                                    TextBlock.fontSize 16.0
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground Brushes.White
                                ]
                                TextBlock.create [
                                    TextBlock.text "Optional \u2014 add a comment to explain your rating."
                                    TextBlock.fontSize 11.0
                                    TextBlock.foreground (SolidColorBrush(Color.Parse("#71717A")))
                                ]
                                TextBox.create [
                                    TextBox.text fb.Comment
                                    TextBox.watermark "Tell us more (optional)..."
                                    TextBox.acceptsReturn true
                                    TextBox.textWrapping TextWrapping.Wrap
                                    TextBox.height 96.0
                                    TextBox.isEnabled (not fb.Submitting)
                                    TextBox.onTextChanged (fun t -> dispatch (FeedbackCommentChanged t))
                                ]
                                StackPanel.create [
                                    StackPanel.orientation Orientation.Horizontal
                                    StackPanel.horizontalAlignment HorizontalAlignment.Right
                                    StackPanel.spacing 8.0
                                    StackPanel.children [
                                        Button.create [
                                            Button.content "Cancel"
                                            Button.isEnabled (not fb.Submitting)
                                            Button.onClick (fun _ -> dispatch FeedbackCancelled)
                                        ]
                                        Button.create [
                                            Button.content (if fb.Submitting then "Sending..." else "Submit")
                                            Button.isEnabled (not fb.Submitting)
                                            Button.onClick (fun _ -> dispatch FeedbackSubmitPressed)
                                        ]
                                    ]
                                ]
                            ]
                        ]
                    )
                ]
            )
        ]

    let view (dispatch: Msg -> unit) (model: SessionState) : Avalonia.FuncUI.Types.IView =
        let canSend =
            model.Chat = Idle
            && model.History = Loaded

        let mainContent : Avalonia.FuncUI.Types.IView =
          DockPanel.create [
            DockPanel.lastChildFill true
            DockPanel.children [
                // Input area at bottom
                Border.create [
                    Border.dock Dock.Bottom
                    Border.padding (12.0, 8.0)
                    Border.borderThickness (0.0, 1.0, 0.0, 0.0)
                    Border.borderBrush (SolidColorBrush(Color.Parse("#3F3F46")))
                    Border.child (
                        DockPanel.create [
                            DockPanel.lastChildFill true
                            DockPanel.children [
                                Button.create [
                                    Button.dock Dock.Right
                                    Button.content "Send"
                                    Button.margin (8.0, 0.0, 0.0, 0.0)
                                    Button.verticalAlignment VerticalAlignment.Bottom
                                    Button.isEnabled canSend
                                    Button.onClick (fun _ ->
                                        dispatch SendPressed)
                                ]
                                TextBox.create [
                                    TextBox.text model.Input
                                    TextBox.watermark "Type a message... (Enter to send)"
                                    TextBox.acceptsReturn false
                                    TextBox.minHeight 36.0
                                    TextBox.verticalAlignment VerticalAlignment.Bottom
                                    TextBox.isEnabled canSend
                                    TextBox.onTextChanged (fun text ->
                                        dispatch (InputChanged text))
                                ]
                            ]
                              ]
                          )
                ]

                // Messages area
                ScrollViewer.create [
                    ScrollViewer.padding (12.0, 8.0)
                    ScrollViewer.verticalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                    ScrollViewer.content (
                        StackPanel.create [
                            StackPanel.orientation Orientation.Vertical
                            StackPanel.spacing 8.0
                            StackPanel.children [
                                if model.History = Loading then
                                    StackPanel.create [
                                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                                        StackPanel.margin (0.0, 40.0, 0.0, 0.0)
                                        StackPanel.spacing 10.0
                                        StackPanel.children [
                                            ProgressBar.create [
                                                ProgressBar.width 220.0
                                                ProgressBar.height 8.0
                                                ProgressBar.isIndeterminate true
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Loading conversation history..."
                                                TextBlock.foreground (SolidColorBrush(Color.Parse("#A1A1AA")))
                                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                            ]
                                        ]
                                    ]
                                elif model.Messages.IsEmpty then
                                    TextBlock.create [
                                        TextBlock.text "Start a conversation..."
                                        TextBlock.foreground (SolidColorBrush(Color.Parse("#71717A")))
                                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                        TextBlock.margin (0.0, 40.0, 0.0, 0.0)
                                    ]
                                else
                                    for msg in model.Messages do
                                        Border.create [
                                            Border.padding (8.0, 6.0)
                                            Border.cornerRadius 6.0
                                            Border.background (
                                                if msg.Role = "You"
                                                then SolidColorBrush(Color.Parse("#27272A")) :> IBrush
                                                else SolidColorBrush(Color.Parse("#1E293B")) :> IBrush)
                                            Border.horizontalAlignment (
                                                if msg.Role = "You"
                                                then HorizontalAlignment.Right
                                                else HorizontalAlignment.Left)
                                            Border.maxWidth 600.0
                                            Border.child (
                                                StackPanel.create [
                                                    StackPanel.children [
                                                        TextBlock.create [
                                                            TextBlock.text msg.Role
                                                            TextBlock.fontSize 11.0
                                                            TextBlock.foreground (SolidColorBrush(Color.Parse("#A1A1AA")))
                                                        ]
                                                        SelectableTextBlock.create [
                                                            SelectableTextBlock.text msg.Content
                                                            SelectableTextBlock.textWrapping TextWrapping.Wrap
                                                            SelectableTextBlock.foreground Brushes.White
                                                        ]
                                                        if msg.Role = "Nao" then
                                                            match msg.FeedbackGiven with
                                                            | Some s ->
                                                                TextBlock.create [
                                                                    TextBlock.text (if s = "positive" then "\U0001F44D Thanks for your feedback" else "\U0001F44E Thanks for your feedback")
                                                                    TextBlock.fontSize 11.0
                                                                    TextBlock.margin (0.0, 6.0, 0.0, 0.0)
                                                                    TextBlock.foreground (SolidColorBrush(Color.Parse("#A1A1AA")))
                                                                ]
                                                            | None ->
                                                                StackPanel.create [
                                                                    StackPanel.orientation Orientation.Horizontal
                                                                    StackPanel.spacing 6.0
                                                                    StackPanel.margin (0.0, 6.0, 0.0, 0.0)
                                                                    StackPanel.children [
                                                                        Button.create [
                                                                            Button.content "\U0001F44D"
                                                                            Button.fontSize 12.0
                                                                            Button.padding (8.0, 2.0)
                                                                            Button.onClick (fun _ -> dispatch (FeedbackRequested (msg.MessageId, "positive")))
                                                                        ]
                                                                        Button.create [
                                                                            Button.content "\U0001F44E"
                                                                            Button.fontSize 12.0
                                                                            Button.padding (8.0, 2.0)
                                                                            Button.onClick (fun _ -> dispatch (FeedbackRequested (msg.MessageId, "negative")))
                                                                        ]
                                                                    ]
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

        Panel.create [
            Panel.children [
                mainContent
                if model.Feedback.IsSome then
                    feedbackOverlay dispatch model.Feedback.Value
            ]
        ]

