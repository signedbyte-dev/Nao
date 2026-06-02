namespace Nao.Agents

open System.Threading.Tasks

/// Abstract interface for an agent
type IAgent =
    abstract member Id: AgentId
    abstract member RunAsync: string -> Task<string>
    abstract member HandleMessageAsync: AgentMessage -> Task<AgentMessage option>
    abstract member State: AgentState
