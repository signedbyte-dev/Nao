# Nao.Demo — Interactive Personal Assistant CLI

A complete reference implementation of the Nao agent framework, demonstrating how to build an interactive CLI assistant with tool invocation, governance, and observability.

## Quick Start

```bash
# Start a local Ollama server (required)
ollama serve

# Run with default model (qwen2.5:3b)
dotnet run --project src/Nao.Demo

# Run with a specific model
dotnet run --project src/Nao.Demo -- llama3.2:3b
```

## Features Demonstrated

| Feature | Implementation |
|---------|---------------|
| **LLM Integration** | OllamaProvider via `ProviderFactory` |
| **Tool Invocation** | Orchestrator parses JSON actions, invokes tools |
| **File System Tools** | `create_folder`, `write_file`, `read_file`, `list_folder`, `delete` |
| **Utility Tools** | `get_datetime`, `calculator` |
| **Verify & Revert** | Tools include `Verify` and `Revert` functions |
| **Content Metadata** | `OutputContentType = ContentMeta.Json` on tools |
| **Execution Journal** | `InMemoryExecutionJournal` records all tool calls |
| **ETCLOVG Harness** | Full 7-layer harness wraps each request |
| **Constitution** | Safety rules block credential leaks |
| **Structured Prompts** | `Prompt` record with Role, Objective, Constraints |
| **Event Sink** | Console sink shows orchestration in real-time |

## Project Structure

```
src/Nao.Demo/
├── Nao.Demo.fsproj          # Console app, references Core/Agents/Providers/Loader
├── src/
│   ├── DemoTools.fs          # File-system & utility tools (verify, revert, ContentMeta)
│   ├── DemoAgent.fs          # Agent creation with Orchestrator + Prompt
│   └── Program.fs            # CLI entry point with interactive loop & ETCLOVG
└── .nao/
    ├── tools/                # JSON tool definitions (echo, word_count)
    ├── agents/               # Agent configuration
    └── governance/           # Constitution rules
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `/help` | Show available commands |
| `/workspace` | Show the workspace directory path |
| `/journal` | Display the execution journal (tool call history) |
| `/undo` | Revert the last tool action |
| `/quit` | Exit the CLI |

## Architecture

```
User Input
    │
    ▼
┌─────────────────────────────┐
│  ETCLOVG Harness            │  ← Governance, observability, verification
│  ┌───────────────────────┐  │
│  │  Orchestrator          │  │  ← LLM reasoning + action parsing
│  │  ┌─────────────────┐  │  │
│  │  │  OllamaProvider  │  │  │  ← Local LLM via HTTP
│  │  └─────────────────┘  │  │
│  │  ┌─────────────────┐  │  │
│  │  │  Tool Execution  │  │  │  ← FileSystem/System tools
│  │  └─────────────────┘  │  │
│  └───────────────────────┘  │
│  ┌───────────────────────┐  │
│  │  Execution Journal     │  │  ← Undo support
│  └───────────────────────┘  │
│  ┌───────────────────────┐  │
│  │  Constitution Check    │  │  ← Safety rules
│  └───────────────────────┘  │
└─────────────────────────────┘
    │
    ▼
Console Output
```

## Extending

- **Add tools**: Create new `Tool` values in `DemoTools.fs` or add JSON definitions in `.nao/tools/`
- **Change model**: Pass model name as CLI argument or modify `OllamaConfig.Default`
- **Add governance rules**: Edit `.nao/governance/constitution.json`
- **Add memory**: Set `OrchestratorMemoryConfig.WithWindow` in `DemoAgent.fs`
