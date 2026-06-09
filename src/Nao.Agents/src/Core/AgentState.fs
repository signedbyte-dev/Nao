namespace Nao.Agents

open Nao.Core

/// The current state of an agent, tracking conversation history and key-value memory
type AgentState =
    { /// Full conversation history (system, user, and assistant messages)
      Conversation: Conversation
      /// Key-value memory store for persisting facts across turns
      Memory: Map<string, string> }

    /// An empty initial state with no conversation or memory
    static member Empty =
        { Conversation = []
          Memory = Map.empty }

