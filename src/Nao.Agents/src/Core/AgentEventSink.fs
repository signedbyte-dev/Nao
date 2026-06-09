namespace Nao.Agents

open System

/// A sink that receives agent events. Replaces separate IAgentLogger,
/// OnProgress callbacks, and implicit conversation tracking.
type IAgentEventSink =
    /// Emit a single event into the sink
    abstract member Emit: AgentEvent -> unit

/// Composable event sink implementations
module AgentEventSink =

    /// A sink that discards all events (no-op)
    let none : IAgentEventSink =
        { new IAgentEventSink with
            member _.Emit _ = () }

    /// A sink that forwards events to multiple child sinks
    let combine (sinks: IAgentEventSink list) : IAgentEventSink =
        { new IAgentEventSink with
            member _.Emit event =
                for sink in sinks do
                    sink.Emit event }

    /// A sink that only forwards events at or above the given log level
    let filter (minLevel: LogLevel) (inner: IAgentEventSink) : IAgentEventSink =
        let levelOrder = function
            | LogLevel.Trace -> 0
            | LogLevel.Debug -> 1
            | LogLevel.Info -> 2
            | LogLevel.Warning -> 3
            | LogLevel.Error -> 4
        { new IAgentEventSink with
            member _.Emit event =
                if levelOrder event.Level >= levelOrder minLevel then
                    inner.Emit event }

    /// A sink that prints events to the console
    let console (source: string) : IAgentEventSink =
        { new IAgentEventSink with
            member _.Emit event =
                let ts = DateTimeOffset.UtcNow.ToString("o")
                match event with
                | AgentEvent.Log (level, src, msg, data) ->
                    if data.IsEmpty then
                        printfn "[%s] [%A] [%s] %s" ts level src msg
                    else
                        let dataStr =
                            data |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%O" k v) |> String.concat ", "
                        printfn "[%s] [%A] [%s] %s {%s}" ts level src msg dataStr
                | AgentEvent.Thinking round ->
                    printfn "[%s] [DEBUG] [%s] Thinking (round %d)" ts source round
                | AgentEvent.InvokingTool (name, input) ->
                    printfn "[%s] [INFO] [%s] Invoking tool: %s(%s)" ts source name input
                | AgentEvent.ToolResult (name, result) ->
                    printfn "[%s] [INFO] [%s] Tool result from %s: %s" ts source name (result.Substring(0, min 100 result.Length))
                | AgentEvent.DelegatingToAgent (name, input) ->
                    printfn "[%s] [INFO] [%s] Delegating to agent: %s" ts source name
                | AgentEvent.AgentResult (name, result) ->
                    printfn "[%s] [INFO] [%s] Agent result from %s: %s" ts source name (result.Substring(0, min 100 result.Length))
                | AgentEvent.RoundError msg ->
                    printfn "[%s] [WARN] [%s] %s" ts source msg
                | AgentEvent.MaxRoundsReached rounds ->
                    printfn "[%s] [WARN] [%s] Max rounds reached (%d)" ts source rounds
                | AgentEvent.Completed answer ->
                    printfn "[%s] [INFO] [%s] Completed: %s" ts source (answer.Substring(0, min 80 answer.Length))
                | AgentEvent.MessageAdded (role, content) ->
                    printfn "[%s] [TRACE] [%s] Message added (%A): %s" ts source role (content.Substring(0, min 60 content.Length))
                | AgentEvent.ConversationCleared ->
                    printfn "[%s] [TRACE] [%s] Conversation cleared" ts source }

    /// A sink that collects all events into a ResizeArray (for testing/inspection)
    let collect (events: ResizeArray<AgentEvent>) : IAgentEventSink =
        { new IAgentEventSink with
            member _.Emit event = events.Add event }

    /// Adapt an IAgentEventSink to the legacy IAgentLogger interface
    let toLogger (source: string) (sink: IAgentEventSink) : IAgentLogger =
        { new IAgentLogger with
            member _.Log level message =
                sink.Emit (AgentEvent.Log (level, source, message, Map.empty))
            member _.LogWith level message data =
                sink.Emit (AgentEvent.Log (level, source, message, data)) }
