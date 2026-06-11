namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Core

/// Context provided to a tool's Revert function so it can undo its effects
type RevertContext =
    { /// The input that was given to the tool
      Input: string
      /// The output the tool produced
      Output: string
      /// When the tool was executed
      ExecutedAt: DateTimeOffset
      /// Additional metadata from execution
      Metadata: Map<string, string> }

/// A tool that an agent can invoke to perform actions or retrieve information.
/// Supports optional capabilities: content-type declaration, verify, and revert.
type Tool =
    { /// Unique name used by the agent to reference this tool
      Name: string
      /// Human-readable description shown to the LLM so it knows when to use the tool
      Description: string
      /// Execute the tool with a string input and return the result
      Execute: string -> Task<string>
      /// Declared content type of the tool's output (framework carries, does not interpret)
      OutputContentType: ContentMeta
      /// Verify the output is correct given the input. Returns Ok or Error with reason.
      Verify: (string -> string -> Task<Result<unit, string>>) option
      /// Revert/undo changes the tool has made to external resources.
      Revert: (RevertContext -> Task<Result<unit, string>>) option }

    /// Create a simple tool with just name, description, and execute (text/plain, no revert)
    static member Create(name, description, execute) =
        { Name = name
          Description = description
          Execute = execute
          OutputContentType = ContentMeta.Text
          Verify = None
          Revert = None }

    /// Whether this tool declares revert capability
    member this.CanRevert = this.Revert.IsSome

    /// Whether this tool declares verify capability
    member this.CanVerify = this.Verify.IsSome
