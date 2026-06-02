namespace Nao.Agents

open Nao.Core

/// The current state of an agent
type AgentState =
    { Conversation: Conversation
      Memory: Map<string, string> }

    static member Empty =
        { Conversation = []
          Memory = Map.empty }

