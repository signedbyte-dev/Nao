# Nao — F# Agent Framework

Nao is an F# framework for building, orchestrating, and evaluating LLM-powered agents with production-grade governance, observability, and verification.

## Projects

| Project | Description |
|---------|-------------|
| [Nao.Core](reference/nao-core.html) | Core domain types — messages, roles, content metadata, LLM provider interface |
| [Nao.Agents](reference/nao-agents.html) | Agent framework — ETCLOVG harness, tools (verify/revert), execution journal, orchestration |
| [Nao.Providers](reference/nao-providers.html) | LLM provider implementations (Ollama, OpenAI, Anthropic, vLLM, llama.cpp) |
| [Nao.Loader](reference/nao-loader.html) | Workspace loader — JSON definitions, multi-mode execution (Process/HTTP/Custom), plugins |
| [Nao.Eval](reference/nao-eval.html) | Agent evaluation framework — test cases, evaluators, LLM judge, regression |
| [Nao.Runtime.Orleans](reference/nao-runtime-orleans.html) | Distributed runtime — multi-workspace registry, group directory, session grains |

## Architecture

The framework implements the **ETCLOVG** seven-layer taxonomy for structured agent execution:

```text
┌─────────────────────────────────────────────────────────────────────┐
│                     Nao.Runtime.Orleans                               │
│  WorkspaceRegistry · SessionGrain · GroupDirectoryGrain · Persistence  │
├─────────────────────────────────────────────────────────────────────┤
│                        Nao.Loader                                    │
│  JSON Definitions · Multi-mode Execution · Assembly Plugins            │
├─────────────────────────────────────────────────────────────────────┤
│                        Nao.Eval                                      │
│  EvalCase · IEvaluator · LlmJudge · EvalReport · Regression         │
├─────────────────────────────────────────────────────────────────────┤
│                        Nao.Agents (ETCLOVG)                          │
│ ┌───────┐ ┌──────┐ ┌─────────┐ ┌───────────┐ ┌─────────┐ ┌──────┐ │
│ │E:Exec │ │T:Tool│ │C:Context│ │L:Lifecycle│ │O:Observe│ │V:Veri│ │
│ │Sandbox│ │Proto │ │Memory   │ │Pipeline   │ │Trace    │ │Regres│ │
│ │Limits │ │Schema│ │Compact  │ │Hooks      │ │Metrics  │ │Judge │ │
│ └───────┘ └──────┘ └─────────┘ └───────────┘ └─────────┘ └──────┘ │
│ ┌────────────────────────────────────────────────────────────────┐  │
│ │G: Governance — Permission · Constitution · AuditLog · Policy  │  │
│ └────────────────────────────────────────────────────────────────┘  │
│ ┌────────────────────────────────────────────────────────────────┐  │
│ │               EtclovgHarness (integrates all layers)           │  │
│ └────────────────────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────────────┤
│                        Nao.Core                                      │
│  Message · Role · ContentMeta · CompletionOptions · ILlmProvider      │
└─────────────────────────────────────────────────────────────────────┘
```

## ETCLOVG Layers

### E — Execution Environment

Resource-bounded sandboxed agent execution. Enforces time limits, LLM call budgets, token caps, and cost ceilings.

- `ResourceLimits` — Budget constraints (duration, LLM calls, tokens, cost, tool calls)
- `ExecutionContext` — Mutable usage tracker with execution ID for correlation
- `IExecutionEnvironment` — Executes agents within sandbox limits

### T — Tool Interface & Protocol

MCP-inspired structured tool discovery and invocation with middleware:

- `IToolProtocol` — List, discover, invoke tools with structured results
- `ToolSchema` — Rich tool metadata (parameters, examples, cost category)
- `IToolMiddleware` — Pre/post-processing (rate limiting, auditing, transformation)
- `ToolRouter` — Pattern-based or name-based tool selection
- `ContentMeta` — Generic content-type tag on tool outputs (text, JSON, PDF, images, etc.)
- `Tool.Verify` — Optional function to check output correctness
- `Tool.Revert` — Optional function to undo side-effects (with `RevertContext`)
- `ExecutionJournal` — Immutable log of tool executions; supports bulk revert of revertible operations

### C — Context & Memory

Tiered memory management and context compaction:

- `MemoryTier` — ShortTerm, MidTerm, LongTerm with promotion policies
- `ContextCompaction` — DropOldest, Summarize, RelevanceFilter, Hierarchical strategies
- `ConversationWindow` — LastN, TokenBudget, SummarizeAfter windowing
- `ISemanticMemory` — Embedding-based retrieval

### L — Lifecycle & Orchestration

Agent lifecycle state machine and multi-stage pipelines:

- `AgentLifecycle` — Created → Ready → Running → Suspended → Completed/Failed
- `ILifecycleHook` — OnBeforeInit, OnBeforeStep, OnCompleted, OnFailed
- `LifecyclePipeline` — Multi-stage execution with validation and `RetryPolicy`
- `Router`, `Pipeline`, `AgentGroup` — Multi-agent orchestration patterns

### O — Observability & Operations

Distributed tracing, cost metrics, and resilience:

- `ITracer` — OpenTelemetry-style spans with parent/child relationships
- `IMetricsCollector` — LLM call counts, token usage, latency percentiles, cost estimation
- `RetryPolicy` — None, Fixed, ExponentialBackoff, Custom
- `CircuitBreaker` — Failure threshold, open duration, half-open recovery
- `FallbackStrategy` — DefaultValue, Alternative, Cached

### V — Verification & Evaluation

Pre-flight readiness, execution traces, quality judgement, and regression detection:

- `IReadinessCheck` — Validate prerequisites before execution
- `ExecutionTrace` — Full step-by-step execution history (LLM calls, tool invocations)
- `IJudge` — Automated quality judgement with criteria scores
- `Regression.detect` — Compare traces for latency, quality, cost regressions

### G — Governance & Security

Permissions, constitutional rules, audit logging, and runtime policy enforcement:

- `PermissionModel` — Permissive/Restrictive with per-capability grants
- `Constitution` — Declarative output rules (PII detection, harm prevention, domain rules)
- `IAuditLog` — Full audit trail of all agent actions
- `PolicyEngine` — Runtime budget/rate-limit enforcement with Block/Warn/Modify actions
- `HarnessError` — Structured error DU (PermissionDenied, PolicyBlocked, NotReady, etc.)

## Key Concepts

### Agents are Stateless

The runtime (Orleans grains) owns all state — conversation history, memory entries, and session metadata. Agents are created fresh per request:

1. Grain loads persisted conversation from storage
2. Grain creates a fresh agent instance via `DefinitionBuilder`
3. Agent processes the input and returns a response
4. Grain persists the updated conversation; agent is discarded

### Workspace Definitions

Agents, tools, eval suites, and governance configs can be defined as JSON in the `.nao/` directory:

```text
.nao/
├── agents/        ← Agent definitions (prompt, model, tools, sub-agents)
├── tools/         ← Tool definitions (process, HTTP, or custom executors)
├── evals/         ← Evaluation suite definitions
└── governance/    ← Constitution rules, permissions
```

Or discovered from .NET assemblies in the `plugins/` directory.

Tools support three execution strategies via `ToolExecutionDef`:

| Mode | JSON field | Use case |
|------|-----------|----------|
| **Process** | `command` + `args` | Run any executable (bash, python, node, .exe) |
| **HTTP** | `url` + `method` + `headers` | Call REST APIs, webhooks |
| **Custom** | `executor` + `config` | Use registered `IToolExecutor` (gRPC, MCP, etc.) |

Tools can also declare `verify_command`/`revert_command` for correctness checks and rollback.

### Multi-Workspace Runtime

Multiple isolated workspaces can coexist within a single Orleans silo:

- `WorkspaceRegistry` — Thread-safe registry of loaded workspace definitions
- Dynamic registration — Add/remove/reload workspaces at runtime
- Session isolation — Each session is bound to a specific workspace key
- Hot-reload — `ReloadAsync` reloads a workspace from its source path

### Group Directory

Organizational multi-tenancy for teams and enterprises:

- `GroupDirectoryGrain` — Manages members, sessions, and workspace defaults per group
- Role-based membership — Members have roles (admin, member, etc.)
- Session ownership — Track which sessions belong to which users and groups
- Default workspace — Groups can set a default workspace for new sessions

### Orchestration

The `Orchestrator` processes multi-turn interactions by:
- Parsing LLM responses into typed `AgentAction` values
- Executing tool calls and feeding results back
- Delegating to sub-agents when appropriate
- Enforcing round limits to prevent infinite loops

The `EtclovgHarness` wraps orchestration with all seven layers for production use.

## Getting Started

```bash
# Restore tools
dotnet tool restore

# Build
dotnet build

# Run tests (303 tests across 7 projects)
dotnet test

# Generate documentation
dotnet fsdocs build --output docs/output
```

## API Reference

API documentation is auto-generated from XML doc comments in the source code using [FSharp.Formatting](https://fsprojects.github.io/FSharp.Formatting/).
