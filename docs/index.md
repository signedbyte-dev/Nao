# Nao — F# Agent Framework

Nao is an F# framework for building, orchestrating, and evaluating LLM-powered agents.

## Projects

| Project | Description |
|---------|-------------|
| [Nao.Core](reference/nao-core.html) | Core domain types — messages, roles, completion options, LLM provider interface |
| [Nao.Agents](reference/nao-agents.html) | Agent framework — prompts, tools, orchestration, memory, messaging |
| [Nao.Providers](reference/nao-providers.html) | LLM provider implementations (Ollama, OpenAI, Anthropic, vLLM, llama.cpp) |
| [Nao.Loader](reference/nao-loader.html) | Workspace loader — JSON definitions and .NET assembly plugin discovery |
| [Nao.Eval](reference/nao-eval.html) | Agent evaluation framework — test cases, evaluators, LLM judge |
| [Nao.Runtime.Orleans](reference/nao-runtime-orleans.html) | Distributed runtime via Microsoft Orleans — session grains, state persistence |

## Architecture

```text
┌─────────────────────────────────────────────────────────┐
│                    Nao.Runtime.Orleans                    │
│  SessionGrain · SessionDirectoryGrain · State Persistence│
├─────────────────────────────────────────────────────────┤
│                       Nao.Loader                         │
│  JSON Definitions · Assembly Plugins · WorkspaceLoader   │
├─────────────────────────────────────────────────────────┤
│                       Nao.Agents                         │
│  Orchestrator · Tools · Prompts · Memory · Messaging     │
├─────────────────────────────────────────────────────────┤
│                       Nao.Core                           │
│  Message · Role · CompletionOptions · ILlmProvider       │
└─────────────────────────────────────────────────────────┘
```

## Key Concepts

### Agents are Stateless

The runtime (Orleans grains) owns all state — conversation history, memory entries, and session metadata. Agents are created fresh per request:

1. Grain loads persisted conversation from storage
2. Grain creates a fresh agent instance via `DefinitionBuilder`
3. Agent processes the input and returns a response
4. Grain persists the updated conversation; agent is discarded

### Workspace Definitions

Agents, tools, and eval suites can be defined as JSON in the `.nao/` directory:

```text
.nao/
├── agents/     ← Agent definitions (prompt, model, tools, sub-agents)
├── tools/      ← Tool definitions (command, args)
└── evals/      ← Evaluation suite definitions
```

Or discovered from .NET assemblies in the `plugins/` directory.

### Orchestration

The `Orchestrator` processes multi-turn interactions by:
- Parsing LLM responses into typed `AgentAction` values
- Executing tool calls and feeding results back
- Delegating to sub-agents when appropriate
- Enforcing round limits to prevent infinite loops

## Getting Started

```bash
# Restore tools
dotnet tool restore

# Build
dotnet build

# Run tests
dotnet test

# Generate documentation
dotnet fsdocs build --output docs/output
```

## API Reference

API documentation is auto-generated from XML doc comments in the source code using [FSharp.Formatting](https://fsprojects.github.io/FSharp.Formatting/).
