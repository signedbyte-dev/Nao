namespace Nao.Loader

open Nao.Agents
open Nao.Core
open Nao.Eval

/// Parsed agent definition loaded from JSON configuration.
/// References tools and sub-agents by name; resolved at build time.
type AgentDef =
    { /// Unique name identifying this agent definition
      Name: string
      /// Human-readable description of the agent's purpose
      Description: string
      /// LLM provider name (e.g. "ollama", "openai", "anthropic")
      Provider: string
      /// Model identifier (e.g. "llama3", "gpt-4o")
      Model: string
      /// Structured prompt defining the agent's behavior
      Prompt: Prompt
      /// Tool names this agent can invoke (resolved from workspace)
      Tools: string list
      /// Sub-agent names for delegation (resolved from workspace)
      SubAgents: string list
      /// LLM completion options (temperature, max tokens, etc.)
      Options: CompletionOptions
      /// Maximum orchestration rounds before forcing a response
      MaxRounds: int }

/// How a tool executes (the mechanism for running the tool)
type ToolExecutionDef =
    /// Execute a program/process. Works with any executable: bash, python, node, .exe, etc.
    | Process of command: string * args: string list
    /// Call an HTTP endpoint. The input is sent as the request body.
    | Http of url: string * method: string * headers: Map<string, string>
    /// Use a named executor registered at runtime (for custom protocols: gRPC, MCP, etc.)
    | Custom of executor: string * config: Map<string, string>

/// Parsed tool definition loaded from JSON configuration.
/// Supports multiple execution strategies: process, HTTP, custom.
type ToolDef =
    { /// Unique name identifying this tool
      Name: string
      /// Human-readable description shown to agents
      Description: string
      /// How this tool executes
      Execution: ToolExecutionDef
      /// Content type of the tool's output (e.g. "text/plain", "application/json")
      OutputContentType: string
      /// Optional execution definition for verifying output
      VerifyExecution: ToolExecutionDef option
      /// Optional execution definition for reverting changes
      RevertExecution: ToolExecutionDef option }

/// Reference to an evaluator used in an eval suite
type EvaluatorRef =
    { /// Evaluator type (e.g. "llm-judge", "pattern", "keyword")
      Type: string
      /// Evaluation criteria description (for LLM judge)
      Criteria: string
      /// Score scale (e.g. "1-5", "pass/fail")
      Scale: string
      /// Regex pattern for pattern-based evaluation
      Pattern: string
      /// Required keywords for keyword-based evaluation
      Keywords: string list }

/// Parsed eval suite definition grouping test cases with an evaluator
type EvalSuiteDef =
    { /// Unique name for this evaluation suite
      Name: string
      /// Description of what this suite evaluates
      Description: string
      /// Agent name to evaluate (resolved from workspace)
      Agent: string
      /// Evaluator configuration for scoring results
      Evaluator: EvaluatorRef
      /// Test cases to run against the agent
      Cases: EvalCase list }

/// Parsed constitution rule definition loaded from JSON
type ConstitutionRuleDef =
    { /// Unique rule identifier
      Id: string
      /// Human-readable description
      Description: string
      /// Category: "Safety", "Privacy", "Behavioral", "Format", or "Domain:<name>"
      Category: string
      /// Priority (higher = more important)
      Priority: int
      /// Whether this is a hard constraint (blocks output) vs soft (warning)
      IsHardConstraint: bool
      /// Regex pattern for the check (content matching this pattern violates the rule)
      Pattern: string }

/// Parsed constitution definition (collection of rules)
type ConstitutionDef =
    { /// Constitution name
      Name: string
      /// Version identifier
      Version: string
      /// Rules in this constitution
      Rules: ConstitutionRuleDef list }

/// Errors that can occur during definition loading
type LoadError =
    /// File path does not exist
    | FileNotFound of path: string
    /// JSON or configuration parsing failed
    | ParseError of source: string * message: string
    /// Definition is syntactically valid but semantically invalid
    | ValidationError of source: string * message: string
    /// A referenced agent, tool, or evaluator was not found
    | MissingReference of kind: string * name: string
    /// .NET assembly could not be loaded or reflected
    | AssemblyLoadError of path: string * message: string

/// Functions for formatting load errors
module LoadError =
    /// Format a LoadError as a human-readable string
    let format error =
        match error with
        | FileNotFound path -> sprintf "File not found: %s" path
        | ParseError (source, msg) -> sprintf "Parse error in %s: %s" source msg
        | ValidationError (source, msg) -> sprintf "Validation error in %s: %s" source msg
        | MissingReference (kind, name) -> sprintf "Missing %s reference: '%s'" kind name
        | AssemblyLoadError (path, msg) -> sprintf "Assembly load error in %s: %s" path msg

/// Result type for load operations
type LoadResult<'T> = Result<'T, LoadError>

/// The result of loading definitions from a source
type LoadedDefinitions =
    { Agents: LoadResult<AgentDef> list
      Tools: LoadResult<ToolDef> list
      EvalSuites: LoadResult<EvalSuiteDef> list
      /// Governance constitution definitions
      Constitutions: LoadResult<ConstitutionDef> list
      /// Pre-built agents discovered directly (e.g. from assemblies)
      BuiltAgents: IAgent list
      /// Pre-built tools discovered directly (e.g. from assemblies)
      BuiltTools: Tool list
      /// Pre-built evaluators discovered directly (e.g. from assemblies)
      BuiltEvaluators: IEvaluator list }

    static member Empty =
        { Agents = []
          Tools = []
          EvalSuites = []
          Constitutions = []
          BuiltAgents = []
          BuiltTools = []
          BuiltEvaluators = [] }

/// A source that can provide agent/tool/eval definitions
type IDefinitionSource =
    /// A human-readable name for this source (e.g. "json:/path/.nao", "assembly:MyPlugin.dll")
    abstract member Name: string
    /// Load all definitions from this source
    abstract member Load: unit -> LoadedDefinitions
