namespace Nao.Core

/// The result of an LLM completion
type CompletionResult =
    { Content: string
      FinishReason: string
      TokensUsed: int option }
