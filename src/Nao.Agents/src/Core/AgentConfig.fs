namespace Nao.Agents

open Nao.Core

/// Configuration for an agent
type AgentConfig =
    { Provider: ILlmProvider
      Prompt: Prompt
      Tools: Tool list
      CompletionOptions: CompletionOptions }
