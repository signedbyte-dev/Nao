namespace Nao.Agents

open System.Threading.Tasks

/// Abstract interface for an agent.
/// Agents process user input, maintain conversational state,
/// and can communicate with other agents via messages.
type IAgent =
    /// Unique identifier for this agent
    abstract member Id: AgentId
    /// Process a user input string and return a response
    abstract member RunAsync: string -> Task<string>
    /// Handle an inter-agent message and optionally reply
    abstract member HandleMessageAsync: AgentMessage -> Task<AgentMessage option>
    /// Current mutable state (conversation history and memory)
    abstract member State: AgentState
