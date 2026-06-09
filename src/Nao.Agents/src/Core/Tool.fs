namespace Nao.Agents

open System.Threading.Tasks

/// A tool that an agent can invoke to perform actions or retrieve information
type Tool =
    { /// Unique name used by the agent to reference this tool
      Name: string
      /// Human-readable description shown to the LLM so it knows when to use the tool
      Description: string
      /// Execute the tool with a string input and return the result
      Execute: string -> Task<string> }
