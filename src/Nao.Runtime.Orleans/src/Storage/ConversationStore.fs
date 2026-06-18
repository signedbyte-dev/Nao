namespace Nao.Runtime.Orleans

open System
open System.Threading.Tasks
open Nao.Core

/// A single persisted message in a conversation
type PersistedMessage =
    { Role: string
      Content: string
      Timestamp: DateTimeOffset }

/// Metadata about a persisted conversation
type ConversationMeta =
    { SessionId: string
      ConversationName: string
      AgentName: string
      CreatedAt: DateTimeOffset
      LastMessageAt: DateTimeOffset
      MessageCount: int }

/// Pluggable interface for external conversation persistence.
/// Implementations can store to files, databases, or cloud storage.
/// All methods are organized by session ID for grouping.
type IConversationStore =
    /// Append messages to a conversation (incremental — does not rewrite the whole history)
    abstract member AppendAsync: sessionId: string -> conversationName: string -> messages: PersistedMessage array -> Task

    /// Save the full conversation (overwrites any existing data for this session+conversation)
    abstract member SaveAsync: sessionId: string -> conversationName: string -> messages: PersistedMessage array -> Task

    /// Load the full conversation history for a session+conversation
    abstract member LoadAsync: sessionId: string -> conversationName: string -> Task<PersistedMessage array>

    /// List all conversations for a session
    abstract member ListConversationsAsync: sessionId: string -> Task<ConversationMeta array>

    /// List all session IDs that have stored conversations
    abstract member ListSessionsAsync: unit -> Task<string array>

    /// Delete a specific conversation
    abstract member DeleteConversationAsync: sessionId: string -> conversationName: string -> Task

    /// Delete all data for a session
    abstract member DeleteSessionAsync: sessionId: string -> Task
