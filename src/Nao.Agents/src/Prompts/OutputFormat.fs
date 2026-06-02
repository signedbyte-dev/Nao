namespace Nao.Agents

/// Output format the agent should produce
type OutputFormat =
    | FreeText
    | Json of schema: string option
    | Markdown
    | Custom of formatInstruction: string
