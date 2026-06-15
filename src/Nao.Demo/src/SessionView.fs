namespace Nao.Demo

open System
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media
open Nao.Demo

/// A single chat session tab view
module SessionView =

    type Message =
        { Role: string
          Content: string
          Timestamp: DateTime }

    type SessionState =
        { Id: string
          ServerSessionId: string option
          Title: string
          Messages: Message list
          Input: string
          IsProcessing: bool }

    let createNew () =
        { Id = Guid.NewGuid().ToString("N").[..7]
          ServerSessionId = None
          Title = "New Session"
          Messages = []
          Input = ""
          IsProcessing = false }

    let private ensureSession (client: NaoClient) (state: IWritable<SessionState>) = task {
        match state.Current.ServerSessionId with
        | Some id -> return id
        | None ->
            let! sessionId = client.CreateSessionAsync(SessionStartRequest.Default)
            state.Set { state.Current with ServerSessionId = Some sessionId }
            return sessionId
    }

    let private sendMessage (client: NaoClient) (state: IWritable<SessionState>) (msg: string) =
        Task.Run<unit>(Func<Task<unit>>(fun () -> task {
            try
                let! sessionId = ensureSession client state
                let! response = client.ChatAsync(sessionId, msg)
                let replyMsg = { Role = "Nao"; Content = response; Timestamp = DateTime.Now }
                state.Set { state.Current with
                                Messages = state.Current.Messages @ [ replyMsg ]
                                IsProcessing = false }
            with ex ->
                let errorMsg = { Role = "System"; Content = sprintf "Error: %s" ex.Message; Timestamp = DateTime.Now }
                state.Set { state.Current with
                                Messages = state.Current.Messages @ [ errorMsg ]
                                IsProcessing = false }
        })) |> ignore

    let view (client: NaoClient) (state: IWritable<SessionState>) =
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
                                    Button.isEnabled (not state.Current.IsProcessing)
                                    Button.onClick (fun _ ->
                                        if not (String.IsNullOrWhiteSpace(state.Current.Input)) then
                                            let msg = state.Current.Input
                                            let userMsg = { Role = "You"; Content = msg; Timestamp = DateTime.Now }
                                            state.Set { state.Current with
                                                            Messages = state.Current.Messages @ [ userMsg ]
                                                            Input = ""
                                                            IsProcessing = true }
                                            sendMessage client state msg)
                                ]
                                TextBox.create [
                                    TextBox.text state.Current.Input
                                    TextBox.watermark "Type a message... (Enter to send)"
                                    TextBox.acceptsReturn false
                                    TextBox.minHeight 36.0
                                    TextBox.verticalAlignment VerticalAlignment.Bottom
                                    TextBox.onTextChanged (fun text ->
                                        state.Set { state.Current with Input = text })
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
                                if state.Current.Messages.IsEmpty then
                                    TextBlock.create [
                                        TextBlock.text "Start a conversation..."
                                        TextBlock.foreground (SolidColorBrush(Color.Parse("#71717A")))
                                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                        TextBlock.margin (0.0, 40.0, 0.0, 0.0)
                                    ]
                                else
                                    for msg in state.Current.Messages do
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
