# Nao

A multi-agent AI framework in F# with structured orchestration, memory management, and Orleans-based distributed runtime.

## Overview

Nao is a framework for building composable AI agents that can reason, collaborate, and persist state. It provides structured prompt engineering, tool invocation, multi-agent orchestration patterns, conversation history management, and semantic memory — all running on Microsoft Orleans for scalable distributed execution.

## Features

- **Multi-Agent Orchestration** — Router, Pipeline, and AgentGroup patterns for composing agents
- **Conversation Memory** — Sliding window, token-budget, and summarization strategies
- **Semantic Memory** — Embedding-based retrieval for long-term agent knowledge
- **Persistent State** — Orleans grain persistence for conversation history and memories across sessions
- **Structured Prompts** — Type-safe prompt engineering with roles, constraints, examples, and output formats
- **Tool Invocation** — Agents can invoke tools and delegate to sub-agents
- **Multi-Provider Support** — Pluggable LLM backends (OpenAI, Anthropic, Azure OpenAI, etc.)
- **F# First** — Immutable records, discriminated unions, and functional composition throughout

## Project Structure

```
Nao.slnx
├── src/
│   ├── Nao.Core/                # Core types: Message, Role, CompletionResult, ILlmProvider
│   ├── Nao.Agents/              # Agent framework
│   │   ├── Core/                # IAgent, AgentId, AgentState, Tool, AgentAction
│   │   ├── Prompts/             # Prompt, PromptExample, OutputFormat
│   │   ├── Messaging/           # AgentMessage for inter-agent communication
│   │   ├── Memory/              # ConversationWindow, MemoryStore, Summarizer, SemanticMemory
│   │   ├── Orchestration/       # Router, Pipeline, AgentGroup
│   │   ├── Logging/             # LogLevel, LogEntry, AgentLogger
│   │   └── Harness/             # AgentHarness for testing/evaluation
│   ├── Nao.Providers/           # LLM provider implementations
│   └── Nao.Runtime.Orleans/     # Distributed runtime
│       ├── Grains/              # AgentGrainBase, StatefulAgentGrainBase, IAgentGrain
│       └── Serialization/       # F# type serialization for Orleans
└── tests/
    ├── Nao.Core.Tests/
    ├── Nao.Agents.Tests/        # ConversationWindow, MemoryStore, Summarizer, SemanticMemory tests
    ├── Nao.Providers.Tests/
    ├── Nao.Runtime.Orleans.Tests/
    └── Nao.E2E.Tests/           # End-to-end orchestration and agent tests
```

## Prerequisites

- .NET 10.0+
- [Paket](https://fsprojects.github.io/Paket/) (installed as a local tool)

## Getting Started

```bash
# Restore tools
dotnet tool restore

# Install dependencies
dotnet paket install

# Build
dotnet build

# Run tests
dotnet test
```

## Architecture

### Agent Model

Every agent implements `IAgent`:

```fsharp
type IAgent =
    abstract member Id: AgentId
    abstract member RunAsync: string -> Task<string>
    abstract member HandleMessageAsync: AgentMessage -> Task<AgentMessage option>
    abstract member State: AgentState
```

Agents can invoke tools, delegate to sub-agents, or respond directly:

```fsharp
type AgentAction =
    | Respond of string
    | InvokeTool of toolName: string * input: string
    | DelegateToAgent of agentName: string * input: string
    | Think of string
```

### Orchestration Patterns

**Router** — A central agent decides which specialist handles the request:

```fsharp
let router = Router.create [ weatherAgent; mathAgent ] (ByPrompt orchestrator)
let result = Router.routeAsync "What's the weather?" router
```

Routing strategies: `ByName`, `ByPrompt` (LLM-decided), `RoundRobin`, `Custom`.

**Pipeline** — Sequential processing through multiple agents:

```fsharp
let pipeline = Pipeline.create [ fetcher; summarizer; formatter ]
let result = Pipeline.runAsync input pipeline
```

**AgentGroup** — Collaborative multi-agent conversation with termination conditions:

```fsharp
let group = AgentGroup.create [ analyst; critic ] (MaxRounds 5)
let history = AgentGroup.runAsync "Analyze this data" group
```

### Memory Management

**Conversation Windowing** — Prevent token overflow:

```fsharp
type WindowStrategy =
    | LastN of int                    // Keep last N messages
    | TokenBudget of maxTokens: int  // Fit within token limit
    | SummarizeAfter of threshold: int // Summarize old messages
```

**Summarization** — LLM-powered condensation of older messages:

```fsharp
let config = SummarizationConfig.Default provider
let trimmed = Summarizer.applyAsync config conversation
```

**Key-Value Memory** — Structured fact storage per agent:

```fsharp
let store = InMemoryStore() :> IMemoryStore
store.SaveAsync agentId { Key = "user-name"; Value = "Alice"; ... }
store.RecallAsync agentId "user"
```

**Semantic Memory** — Embedding-based similarity retrieval:

```fsharp
let memory = InMemorySemanticMemory(embeddingProvider) :> ISemanticMemory
memory.StoreAsync agentId "fact-1" "The capital of France is Paris"
memory.RetrieveAsync agentId "What's the French capital?" topK=3
```

### Orleans Runtime

Agents run as Orleans grains for distributed, persistent execution:

- `AgentGrainBase` — Simple stateless grain wrapper
- `StatefulAgentGrainBase` — Persistent conversation + memory with automatic windowing

```fsharp
type MyAgentGrain(state) =
    inherit StatefulAgentGrainBase(state)
    override _.Agent = myAgent
    override _.WindowStrategy = Some (LastN 50)
```

### Structured Prompts

```fsharp
let prompt =
    { Prompt.Empty with
        Role = "You are a financial analyst."
        Objective = "Analyze quarterly earnings reports."
        Constraints = [ "Use only provided data"; "Be concise" ]
        Examples = [ { Input = "Q1 revenue?"; Output = "$2.3B"; Explanation = None } ]
        OutputFormat = Json (Some """{"summary": "...", "trend": "..."}""") }
```

## Package Management

This project uses Paket for dependency management. To add a package:

1. Edit `paket.dependencies` to add the source package
2. Add the package name to the relevant project's `paket.references`
3. Run `dotnet paket install`

## Git Hooks

A pre-commit hook ensures all tests pass before commits are accepted. It runs `dotnet test` automatically.

## Coding Conventions

### File Organization

- **One type per file** — Each type, interface, or discriminated union gets its own file
- **File names match the primary type** — e.g. `AgentState` lives in `AgentState.fs`
- **Compile order matters** — Files in `.fsproj` are listed in dependency order (dependencies first)

### Naming

- **Types**: PascalCase (`CompletionResult`, `AgentGroup`)
- **Modules**: PascalCase, matching the type they operate on (`module ConversationWindow`)
- **Functions**: camelCase (`applyLastN`, `routeAsync`)
- **DU cases**: PascalCase (`LastN`, `TokenBudget`, `ByPrompt`)
- **Interfaces**: prefix with `I` (`ILlmProvider`, `IAgent`, `IMemoryStore`)

### F# Style

- Prefer discriminated unions over class hierarchies
- Prefer immutable records for data types
- Use `option` instead of null
- Use `Task<T>` for async operations (interop-friendly)
- Keep modules alongside their corresponding type for helper functions
- Use XML doc comments (`///`) for public API types and members

### Project Structure

- Source projects go under `src/`
- Test projects go under `tests/`
- Each source project has a matching `<ProjectName>.Tests` project
- Test projects use MSTest framework
- Dependencies between source projects use `<ProjectReference>`

### Testing

- Test project names: `<ProjectName>.Tests`
- Test framework: MSTest
- One test file per feature or module being tested
- Test methods should be descriptive: `OrchestratorRoutesToWeatherAgent`

## License

TBD
