namespace Nao.Demo

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
open Nao.Demo

/// A single chat session tab view
module SessionView =

    type Message =
        { Role: string
          Content: string
          Timestamp: DateTime }

    /// Lifecycle of the session's conversation history.
    type HistoryStatus =
        | NeedsLoad
        | Loading
        | Loaded

    /// Whether the session is currently awaiting a chat response.
    type ChatStatus =
        | Idle
        | Sending

    type SessionState =
        { Id: string
          ServerSessionId: string option
          Title: string
          Messages: Message list
          Input: string
          Chat: ChatStatus
          History: HistoryStatus }

    let createNew () =
        { Id = Guid.NewGuid().ToString("N").[..7]
          ServerSessionId = None
          Title = "New Session"
          Messages = []
          Input = ""
          Chat = Idle
          History = Loaded }

    type Msg =
        | TriggerHistoryLoad
        | HistoryLoaded of Result<Message list, string>
        | InputChanged of string
        | SendPressed
        | SendCompleted of Result<string * string, string>

    let private toUiMessage (m: MessageDto) : Message =
        let role =
            match (if isNull m.Role then "" else m.Role).ToLowerInvariant() with
            | "user" -> "You"
            | "assistant" -> "Nao"
            | r -> r
        { Role = role
          Content = (if isNull m.Content then "" else m.Content)
          Timestamp = DateTime.Now }

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
                { Role = "System"
                  Content = sprintf "Failed to load history: %s" err
                  Timestamp = DateTime.Now }
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
                let userMsg = { Role = "You"; Content = text; Timestamp = DateTime.Now }
                { model with
                    Messages = model.Messages @ [ userMsg ]
                    Input = ""
                    Chat = Sending },
                sendCmd client model text

        | SendCompleted (Ok (sessionId, response)) ->
            let replyMsg = { Role = "Nao"; Content = response; Timestamp = DateTime.Now }
            { model with
                ServerSessionId = Some sessionId
                Messages = model.Messages @ [ replyMsg ]
                Chat = Idle },
            Cmd.none

        | SendCompleted (Error err) ->
            let errorMsg = { Role = "System"; Content = sprintf "Error: %s" err; Timestamp = DateTime.Now }
            { model with Messages = model.Messages @ [ errorMsg ]; Chat = Idle }, Cmd.none

    let view (dispatch: Msg -> unit) (model: SessionState) : Avalonia.FuncUI.Types.IView =
        let canSend =
            model.Chat = Idle
            && model.History = Loaded

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

