# Nao

An F# AI agent library that combines large language models with logic programming.

## Overview

Nao brings together the generative power of LLMs and the precision of logic programming into a unified agent framework. It enables building AI agents that can reason logically while leveraging natural language understanding from various AI platforms.

## Features

- **Logic Programming Integration** — Combine logical inference with LLM capabilities for structured reasoning
- **Multi-Platform Support** — Plug in different AI backends (OpenAI/ChatGPT, Anthropic, Azure OpenAI, etc.)
- **F# First** — Built entirely in F#, leveraging the type system for safe agent composition

## Project Structure

```
Nao.sln
├── src/
│   ├── Nao.Core/            # Core abstractions and logic engine
│   ├── Nao.Agents/          # Agent framework and orchestration
│   └── Nao.Providers/       # AI platform integrations
└── tests/
    ├── Nao.Core.Tests/      # MSTest tests for Core
    ├── Nao.Agents.Tests/    # MSTest tests for Agents
    └── Nao.Providers.Tests/ # MSTest tests for Providers
```

Each project has a corresponding test project using the MSTest framework.

## Prerequisites

- .NET 9.0+
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

## Package Management

This project uses Paket for dependency management. To add a package:

1. Edit `paket.dependencies` to add the source package
2. Add the package name to the relevant project's `paket.references`
3. Run `dotnet paket install`

## Coding Conventions

### File Organization

- **One type per file** — Each type, interface, or discriminated union gets its own file
- **File names match the primary type** — e.g. `AgentState` lives in `AgentState.fs`
- **Compile order matters** — Files in `.fsproj` are listed in dependency order (dependencies first)

### Naming

- **Types**: PascalCase (`CompletionResult`, `KnowledgeBase`)
- **Modules**: PascalCase, matching the type they operate on (`module KnowledgeBase`)
- **Functions**: camelCase (`addFact`, `create`)
- **DU cases**: PascalCase (`Atom`, `Variable`, `Compound`)
- **Interfaces**: prefix with `I` (`ILlmProvider`, `IAgent`)

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
- Test methods should be descriptive: `CanAddFactToKnowledgeBase`

## License

TBD
