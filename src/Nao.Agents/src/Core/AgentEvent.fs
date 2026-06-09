namespace Nao.Agents

open System
open Nao.Core

/// Unified event type covering logging, progress, and conversation changes.
/// Consumers subscribe to a single stream rather than separate callbacks.
[<RequireQualifiedAccess>]
type AgentEvent =
    // --- Logging ---
    | Log of level: LogLevel * source: string * message: string * data: Map<string, obj>

    // --- Conversation History ---
    | MessageAdded of role: Role * content: string
    | ConversationCleared

    // --- Orchestration Progress ---
    | Thinking of round: int
    | InvokingTool of name: string * input: string
    | ToolResult of name: string * result: string
    | DelegatingToAgent of name: string * input: string
    | AgentResult of name: string * result: string
    | RoundError of message: string
    | MaxRoundsReached of rounds: int
    | Completed of answer: string

    member this.Timestamp = DateTimeOffset.UtcNow

    /// Map an AgentEvent to a LogLevel for filtering purposes
    member this.Level =
        match this with
        | Log (level, _, _, _) -> level
        | MessageAdded _ | ConversationCleared -> LogLevel.Trace
        | Thinking _ -> LogLevel.Debug
        | InvokingTool _ | DelegatingToAgent _ -> LogLevel.Info
        | ToolResult _ | AgentResult _ -> LogLevel.Info
        | Completed _ -> LogLevel.Info
        | RoundError _ -> LogLevel.Warning
        | MaxRoundsReached _ -> LogLevel.Warning
