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

/// Identifies where a tool originated, so feedback-driven adjustments can target
/// the correct source (a JSON file to re-version, or a compiled assembly to patch).
type ToolProvenance =
    { /// Source kind: "json", "assembly", or "code".
      Kind: string
      /// Path to the originating artifact (a JSON file or a DLL), when applicable.
      Location: string option
      /// Optional member identifier within the artifact (e.g. an assembly type/property).
      Member: string option }

/// Helpers for building tool provenance values.
[<RequireQualifiedAccess>]
module ToolProvenance =
    /// Provenance for a tool loaded from a JSON definition file.
    let json (filePath: string) : ToolProvenance =
        { Kind = "json"; Location = Some filePath; Member = None }

    /// Provenance for a tool discovered in a compiled assembly.
    let assembly (dllPath: string) (memberName: string) : ToolProvenance =
        { Kind = "assembly"; Location = Some dllPath; Member = Some memberName }

    /// Provenance for a tool registered directly from code.
    let code (sourceName: string) : ToolProvenance =
        { Kind = "code"; Location = None; Member = Some sourceName }

/// A tool that an agent can invoke to perform actions or retrieve information.
/// Supports optional capabilities: content-type declaration, verify, and revert.
type Tool =
    { /// Unique name used by the agent to reference this tool
      Name: string
      /// Human-readable description shown to the LLM so it knows when to use the tool
      Description: string
      /// Optional version identifier (e.g. "1.0"). None = unversioned; matches any requested version.
      Version: string option
      /// Execute the tool with a string input and return the result
      Execute: string -> Task<string>
      /// Declared content type of the tool's output (framework carries, does not interpret)
      OutputContentType: ContentMeta
      /// Verify the output is correct given the input. Returns Ok or Error with reason.
      Verify: (string -> string -> Task<Result<unit, string>>) option
      /// Revert/undo changes the tool has made to external resources.
      Revert: (RevertContext -> Task<Result<unit, string>>) option
      /// Where this tool came from (used by the feedback/adjust system to target patches).
      Provenance: ToolProvenance option }

    /// Create a simple tool with just name, description, and execute (text/plain, no revert)
    static member Create(name, description, execute) =
        { Name = name
          Description = description
          Version = None
          Execute = execute
          OutputContentType = ContentMeta.Text
          Verify = None
          Revert = None
          Provenance = None }

    /// Whether this tool declares revert capability
    member this.CanRevert = this.Revert.IsSome

    /// Whether this tool declares verify capability
    member this.CanVerify = this.Verify.IsSome

/// Helpers for version-qualified references of the form "name@version".
/// Used to look up a specific version of a tool or agent while remaining
/// backward compatible with plain, unversioned "name" references.
[<RequireQualifiedAccess>]
module VersionRef =

    /// Parse a possibly version-qualified reference "name@version" into
    /// its (name, version option) parts. "name" => (name, None).
    let parse (reference: string) : string * string option =
        if String.IsNullOrEmpty reference then ("", None)
        else
            let idx = reference.IndexOf('@')
            if idx < 0 then (reference, None)
            else
                let name = reference.Substring(0, idx)
                let ver = reference.Substring(idx + 1)
                (name, (if String.IsNullOrEmpty ver then None else Some ver))

    /// Whether an actual version satisfies a requested version.
    /// A request of None matches any actual version (name-only lookup).
    let matches (requested: string option) (actual: string option) : bool =
        match requested with
        | None -> true
        | Some _ -> requested = actual
