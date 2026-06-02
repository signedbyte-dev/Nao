namespace Nao.Runtime.Orleans.Grains

open Orleans
open System.Threading.Tasks

/// Orleans grain interface for an agent actor.
/// Each agent is represented as a grain addressable by its string ID.
type IAgentGrain =
    inherit IGrainWithStringKey

    /// Process an input message and return the agent's response
    abstract member ProcessAsync: input: string -> Task<string>

    /// Send a message to this agent from another agent
    abstract member ReceiveMessageAsync: fromAgent: string -> message: string -> Task<string option>

    /// Get the current agent name/identity
    abstract member GetAgentIdAsync: unit -> Task<string>
