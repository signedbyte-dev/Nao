namespace Nao.Events

open System
open System.Threading.Tasks
open Nao.Agents
open Nao.Feedback

/// Identity carried by every emitted event. The producer fills these in and never decides
/// where the data lands — routing/persistence is entirely the consumer's choice, so a new
/// storage strategy (per session, per category, per workspace, ...) needs no producer change.
type EventScope =
    { /// Owning user (grain key prefix).
      UserId: string
      /// Session id (grain key suffix); for a task sub-session this includes the task id.
      SessionId: string
      /// Active conversation within the session.
      ConversationId: string
      /// Workspace the turn ran against.
      WorkspaceKey: string
      /// The action that produced the event — the turn id.
      ActionId: string
      /// Parent session key when this is a task sub-session (None for a primary session).
      ParentKey: string option
      /// Storage routing key — the full grain key ("userId/sessionId" or ".../taskId").
      SessionKey: string
      /// When the event occurred.
      Timestamp: DateTimeOffset }

    static member Create
        (userId: string, sessionId: string, conversationId: string, workspaceKey: string,
         actionId: string, sessionKey: string, ?parentKey: string) : EventScope =
        { UserId = userId
          SessionId = sessionId
          ConversationId = conversationId
          WorkspaceKey = workspaceKey
          ActionId = actionId
          ParentKey = parentKey
          SessionKey = sessionKey
          Timestamp = DateTimeOffset.UtcNow }

/// One process step (a tool call or sub-agent delegation) of an assistant turn, in a
/// transport-neutral shape so the conversation event stream carries no storage-layer types.
type ConversationStep =
    { Kind: string
      Title: string
      Input: string
      Output: string }

/// A single persisted conversation message in a transport-neutral shape (decoupled from the
/// runtime's storage record so this layer stays dependency-light).
type ConversationMessage =
    { Role: string
      Content: string
      Timestamp: DateTimeOffset
      /// Turn this message belongs to ("" for legacy messages).
      TurnId: string
      /// Process steps for an assistant turn (empty for user / legacy messages).
      Steps: ConversationStep list
      /// Names of files attached to a user message (empty otherwise).
      Attachments: string list }

/// Domain events the system dispatches. Each carries an EventScope plus its payload.
/// Consumers subscribe to the bus and decide how/where to persist, so adding a storage
/// strategy never requires touching the producers. (Grows per phase: feedback, then
/// observability, then conversations.)
type NaoEvent =
    /// A turn finished and produced a recordable TurnRecord (feedback can be analysed
    /// against it later).
    | TurnCompleted of EventScope * TurnRecord
    /// An implicit feedback signal was detected in a user's message about a prior turn.
    | ImplicitFeedbackCaptured of EventScope * Feedback
    /// A single observability signal (trace span, metric, journal/trace/audit record) was
    /// produced while a turn ran. The full fine-grained observability stream flows through
    /// the bus so any consumer can persist/forward it.
    | ObservabilityCaptured of EventScope * ObservabilitySignal
    /// A conversation store write (messages appended/saved, conversation or session deleted)
    /// occurred. The transcript stream flows through the bus so any consumer can persist or
    /// forward it without the producer choosing where it lands.
    | ConversationCaptured of EventScope * ConversationSignal

/// One fine-grained observability write produced by the agent harness during a turn. These
/// mirror the sink interfaces (ITracer / IMetricsCollector / IExecutionJournal / ITraceStore
/// / IAuditLog) so a consumer can route each to whatever store it chooses.
and ObservabilitySignal =
    /// A trace span was started (root trace or child span).
    | SpanStarted of Span
    /// A span was ended with a final status.
    | SpanEnded of Span * SpanStatus
    /// A timestamped event was attached to a span.
    | SpanEventAdded of Span * name: string * attributes: Map<string, string>
    /// Attributes were set on a span.
    | SpanAttributesSet of Span * attributes: Map<string, string>
    /// An LLM call's token counts and latency were recorded.
    | LlmCallRecorded of inputTokens: int * outputTokens: int * latencyMs: int64
    /// A tool invocation's duration and outcome were recorded.
    | ToolCallRecorded of toolName: string * durationMs: int64 * success: bool
    /// A custom metric point was recorded.
    | MetricRecorded of MetricPoint
    /// A tool execution was recorded in the journal.
    | ExecutionRecorded of ExecutionRecord
    /// A journalled execution was marked reverted.
    | ExecutionReverted of ExecutionRecord
    /// An execution trace was saved to the regression trace store.
    | TraceSaved of ExecutionTrace
    /// A governance audit entry was recorded.
    | AuditRecorded of AuditEntry

/// One conversation-store write. Mirrors the store's mutating operations so a consumer can
/// route each to whatever transcript store it chooses.
and ConversationSignal =
    /// Messages were appended to a conversation (incremental, append-only).
    | MessagesAppended of conversationName: string * messages: ConversationMessage list
    /// A full conversation was saved (overwrites any prior history).
    | ConversationSaved of conversationName: string * messages: ConversationMessage list
    /// A conversation was deleted.
    | ConversationDeleted of conversationName: string
    /// All of a session's conversations were deleted.
    | SessionConversationsDeleted

/// A subscriber that receives every published event and persists/forwards it.
type IEventConsumer =
    abstract member HandleAsync: NaoEvent -> Task

/// The single dispatch service producers publish to. Fans each event out to all
/// subscribed consumers; producers hold only this — never a concrete storage type.
type IEventBus =
    abstract member PublishAsync: NaoEvent -> Task
    abstract member Subscribe: IEventConsumer -> unit

/// In-process synchronous bus: publishing awaits every consumer so persistence is
/// deterministic for the desktop app. A failing consumer is isolated (its exception is
/// swallowed) so one bad sink never breaks a producer's turn.
type InMemoryEventBus() =
    let consumers = ResizeArray<IEventConsumer>()
    let gate = obj ()

    interface IEventBus with
        member _.Subscribe(consumer: IEventConsumer) =
            lock gate (fun () -> consumers.Add consumer)

        member _.PublishAsync(evt: NaoEvent) : Task =
            task {
                let snapshot = lock gate (fun () -> consumers.ToArray())
                for c in snapshot do
                    try
                        do! c.HandleAsync evt
                    with _ ->
                        // Isolate consumers: a storage strategy failing must not abort the
                        // producer's turn. (No logger in this layer; swallow by design.)
                        ()
            } :> Task
