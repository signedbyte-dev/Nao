namespace Nao.Core

/// The result of an LLM completion
type CompletionResult =
    { /// The generated text content
      Content: string
      /// Why generation stopped (e.g. "stop", "length", "tool_call")
      FinishReason: string
      /// Number of tokens consumed (prompt + completion), if reported by the provider
      TokensUsed: int option }
