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
open Nao.Assistant.ExecutionTrace

/// A single chat session tab view
module SessionView =

    type Message =
        { MessageId: Guid
          Role: string
          Content: string
          Timestamp: DateTime
          /// The whole process behind an assistant answer (empty for user messages).
          Steps: ExecutionTrace.Step list
          /// Sentiment ("positive"/"negative") once feedback has been submitted for this message.
          FeedbackGiven: string option
          /// File names attached to this message, rendered as chips (content not shown).
          Attachments: string list }

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
          /// Live process steps streamed while the current turn is being processed
          /// (shown as an in-progress assistant bubble); cleared once the turn completes.
          LiveSteps: ExecutionTrace.Step list
          /// Active feedback popup, if the user is currently rating a response.
          Feedback: FeedbackPrompt option
          /// Agent definitions the user can pick from for this session.
          AvailableAgents: string list
          /// The agent this session will use (only applied before the first message).
          SelectedAgent: string
          /// Whether the agent list has been fetched yet.
          AgentsLoaded: bool
          /// A file attached to the next message: (fileName, content).
          AttachedFile: (string * string) option }

    let createNew () =
        { Id = Guid.NewGuid().ToString("N").[..7]
          ServerSessionId = None
          Title = "New Session"
          Messages = []
          Input = ""
          Chat = Idle
          History = Loaded
          LiveSteps = []
          Feedback = None
          AvailableAgents = []
          SelectedAgent = "nao-assistant"
          AgentsLoaded = false
          AttachedFile = None }

    type Msg =
        | TriggerHistoryLoad
        | HistoryLoaded of Result<Message list, string>
        | InputChanged of string
        | SendPressed
        | SendCompleted of Result<string * string, string>
        | StepsUpdated of ExecutionTrace.Step list
        | FeedbackRequested of messageId: Guid * sentiment: string
        | FeedbackCommentChanged of string
        | FeedbackCancelled
        | FeedbackSubmitPressed
        | FeedbackCompleted of Result<Guid * string, string>
        | TriggerAgentsLoad
        | AgentsListLoaded of string list
        | AgentSelected of string
        | AttachFilePressed
        | FileAttached of string * string
        | AttachmentCleared

    /// Legacy marker: older sessions embedded an attached file inside the message text.
    /// Newer messages carry attachment names as structured metadata instead, but we still
    /// parse the marker out of historic messages so their file body is never rendered.
    let private attachmentMarker = "[[NAO_ATTACHMENT:"

    /// Split a legacy stored message back into (visibleText, attachmentNames), hiding the body.
    let private parseAttachments (raw: string) : string * string list =
        if String.IsNullOrEmpty raw || not (raw.Contains attachmentMarker) then
            raw, []
        else
            let visible = raw.Substring(0, raw.IndexOf attachmentMarker).TrimEnd()
            let names =
                System.Text.RegularExpressions.Regex.Matches(raw, @"\[\[NAO_ATTACHMENT:(.*?)\]\]")
                |> Seq.cast<System.Text.RegularExpressions.Match>
                |> Seq.map (fun m -> m.Groups.[1].Value)
                |> Seq.toList
            visible, names

    let private toUiMessage (m: MessageDto) : Message =
        let role =
            match (if isNull m.Role then "" else m.Role).ToLowerInvariant() with
            | "user" -> "You"
            | "assistant" -> "Nao"
            | r -> r
        let steps =
            if isNull (box m.Steps) then []
            else
                m.Steps
                |> Array.map (fun s ->
                    { Kind = (if isNull s.Kind then "" else s.Kind)
                      Name = (if isNull s.Title then "" else s.Title)
                      Args = (if isNull s.Input then "" else s.Input)
                      Result = (if isNull s.Output then "" else s.Output) } : ExecutionTrace.Step)
                |> Array.toList
        let content = if isNull m.Content then "" else m.Content
        let structured = if isNull (box m.Attachments) then [] else m.Attachments |> Array.toList
        // Prefer structured attachment metadata; fall back to the legacy embedded marker.
        let visible, attachments =
            if not structured.IsEmpty then content, structured
            else parseAttachments content
        { MessageId = Guid.NewGuid()
          Role = role
          Content = visible
          Timestamp = DateTime.Now
          Steps = steps
          FeedbackGiven = None
          Attachments = attachments }

    /// Build a fresh chat message with a unique id and no feedback yet.
    let private mkMessage (role: string) (content: string) : Message =
        { MessageId = Guid.NewGuid()
          Role = role
          Content = content
          Timestamp = DateTime.Now
          Steps = []
          FeedbackGiven = None
          Attachments = [] }

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

    let private sendCmd (client: NaoClient) (model: SessionState) (text: string) (attachments: (string * string) list) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! sessionId =
                            task {
                                match model.ServerSessionId with
                                | Some id -> return id
                                | None ->
                                    return! client.CreateSessionAsync(
                                        { SessionStartRequest.Default with AgentName = model.SelectedAgent })
                            }

                        // Stream the live process steps as the server reports them, so the UI
                        // can show "what's been done so far" while the answer is produced.
                        let onSteps (steps: TurnStepDto list) =
                            let uiSteps =
                                steps
                                |> List.map (fun s ->
                                    { Kind = s.Kind; Name = s.Title; Args = s.Input; Result = s.Output } : ExecutionTrace.Step)
                            Dispatcher.UIThread.Post(fun () -> dispatch (StepsUpdated uiSteps))

                        let! response = client.ChatAsync(sessionId, text, attachments, onSteps)
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (SendCompleted (Ok (sessionId, response))))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (SendCompleted (Error ex.Message)))
                }
                :> Task)
            |> ignore)

    let private loadAgentsCmd (client: NaoClient) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! agents = client.ListAgentsAsync()
                        let names = agents |> List.map (fun a -> a.Name)
                        Dispatcher.UIThread.Post(fun () -> dispatch (AgentsListLoaded names))
                    with _ -> ()
                }
                :> Task)
            |> ignore)

    let private attachFileCmd : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            // The file picker must run on the UI thread (it needs the top-level window).
            Dispatcher.UIThread.Post(fun () ->
                (task {
                    try
                        let! picked = UiContext.pickTextFileAsync ()
                        match picked with
                        | Some (name, content) -> dispatch (FileAttached (name, content))
                        | None -> ()
                    with _ -> ()
                }
                :> Task)
                |> ignore))

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
                let attachments =
                    match model.AttachedFile with
                    | Some (name, content) -> [ name, content ]
                    | None -> []
                let attachmentNames = attachments |> List.map fst
                let userMsg = { mkMessage "You" text with Attachments = attachmentNames }
                { model with
                    Messages = model.Messages @ [ userMsg ]
                    Input = ""
                    Chat = Sending
                    LiveSteps = []
                    AttachedFile = None },
                sendCmd client model text attachments

        | SendCompleted (Ok (sessionId, response)) ->
            let replyMsg = mkMessage "Nao" response
            // Reload the persisted transcript so the reply carries its full process
            // (tool/sub-agent steps), which the chat call itself doesn't return.
            { model with
                ServerSessionId = Some sessionId
                Messages = model.Messages @ [ replyMsg ]
                LiveSteps = []
                Chat = Idle },
            loadHistoryCmd client sessionId

        | SendCompleted (Error err) ->
            let errorMsg = mkMessage "System" (sprintf "Error: %s" err)
            { model with Messages = model.Messages @ [ errorMsg ]; LiveSteps = []; Chat = Idle }, Cmd.none

        | StepsUpdated steps ->
            { model with LiveSteps = steps }, Cmd.none

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

        | TriggerAgentsLoad ->
            if model.AgentsLoaded then model, Cmd.none
            else model, loadAgentsCmd client

        | AgentsListLoaded names ->
            { model with AvailableAgents = names; AgentsLoaded = true }, Cmd.none

        | AgentSelected name ->
            { model with SelectedAgent = name }, Cmd.none

        | AttachFilePressed ->
            model, attachFileCmd

        | FileAttached (name, content) ->
            { model with AttachedFile = Some (name, content) }, Cmd.none

        | AttachmentCleared ->
            { model with AttachedFile = None }, Cmd.none

    let view (dispatch: Msg -> unit) (model: SessionState) : Avalonia.FuncUI.Types.IView =
        let canSend =
            model.Chat = Idle
            && model.History = Loaded

        let mainContent : Avalonia.FuncUI.Types.IView =
          Grid.create [
            Grid.rowDefinitions "*,Auto"
            Grid.children [
                // Composer (input area) at the bottom — the ChatInput business
                // component, styled consistently with the rest of the app.
                Grid.create [
                    Grid.row 1
                    Grid.children [
                        ChatInput.render
                            { Text = model.Input
                              Placeholder = (Localization.current ()).ComposerPlaceholder
                              CanSend = canSend
                              AttachedFileName = model.AttachedFile |> Option.map fst
                              Agents = model.AvailableAgents
                              SelectedAgent = model.SelectedAgent
                              AgentSelectable = model.ServerSessionId.IsNone
                              OnTextChanged = fun text -> dispatch (InputChanged text)
                              OnAttach = fun () -> dispatch AttachFilePressed
                              OnClearAttachment = fun () -> dispatch AttachmentCleared
                              OnAgentSelected = fun s -> dispatch (AgentSelected s)
                              OnSend = fun () -> dispatch SendPressed }
                    ]
                ]

                // Messages area
                ScrollViewer.create [
                    Grid.row 0
                    ScrollViewer.verticalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                    ScrollViewer.content (
                        StackPanel.create [
                            // Padding lives on the content (not the ScrollViewer) so it is
                            // part of the scrollable extent — otherwise the bottom of the
                            // last message is clipped when scrolled to the end.
                            StackPanel.margin (12.0, 8.0)
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
                                                TextBlock.text (Localization.current ()).LoadingHistory
                                                TextBlock.foreground Theme.textSecondary
                                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                            ]
                                        ]
                                    ]
                                elif model.Messages.IsEmpty then
                                    TextBlock.create [
                                        TextBlock.text (Localization.current ()).StartConversation
                                        TextBlock.foreground Theme.textMuted
                                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                        TextBlock.margin (0.0, 40.0, 0.0, 0.0)
                                    ]
                                else
                                    for msg in model.Messages do
                                        MessageBubble.render
                                            { Role = msg.Role
                                              IsUser = (msg.Role = "You")
                                              Content = msg.Content
                                              Attachments = msg.Attachments
                                              Header =
                                                // The process (what Nao did) reads above the answer.
                                                if msg.Role = "Nao" && not msg.Steps.IsEmpty then
                                                    [ ExecutionTrace.render msg.Steps ]
                                                else []
                                              Footer =
                                                if msg.Role = "Nao" then
                                                    MessageActions.render
                                                        { FeedbackGiven = msg.FeedbackGiven
                                                          OnFeedback = fun sentiment -> dispatch (FeedbackRequested (msg.MessageId, sentiment)) }
                                                else [] }

                                    // While a turn is processing, show a live in-progress assistant
                                    // bubble: the steps done so far, then a "Working..." indicator.
                                    // It is replaced by the persisted answer once the turn completes.
                                    if model.Chat = Sending then
                                        MessageBubble.render
                                            { Role = "Nao"
                                              IsUser = false
                                              Content = ""
                                              Attachments = []
                                              Header =
                                                if not model.LiveSteps.IsEmpty then
                                                    [ ExecutionTrace.render model.LiveSteps ]
                                                else []
                                              Footer =
                                                [ StackPanel.create [
                                                      StackPanel.orientation Orientation.Horizontal
                                                      StackPanel.spacing 8.0
                                                      StackPanel.margin (0.0, 6.0, 0.0, 0.0)
                                                      StackPanel.children [
                                                          ProgressBar.create [
                                                              ProgressBar.width 120.0
                                                              ProgressBar.height 4.0
                                                              ProgressBar.isIndeterminate true
                                                          ]
                                                          TextBlock.create [
                                                              TextBlock.text (Localization.current ()).Working
                                                              TextBlock.fontSize 11.0
                                                              TextBlock.foreground Theme.textSecondary
                                                              TextBlock.verticalAlignment VerticalAlignment.Center
                                                          ]
                                                      ]
                                                  ] ] }
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
                    let fb = model.Feedback.Value
                    FeedbackDialog.render
                        { Sentiment = fb.Sentiment
                          Comment = fb.Comment
                          Submitting = fb.Submitting
                          OnCommentChanged = fun t -> dispatch (FeedbackCommentChanged t)
                          OnCancel = fun () -> dispatch FeedbackCancelled
                          OnSubmit = fun () -> dispatch FeedbackSubmitPressed }
            ]
        ]

