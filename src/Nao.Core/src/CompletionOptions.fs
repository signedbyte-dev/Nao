namespace Nao.Core

/// Configuration for an LLM completion request
type CompletionOptions =
    { Temperature: float
      MaxTokens: int option
      StopSequences: string list }

    static member Default =
        { Temperature = 0.7
          MaxTokens = None
          StopSequences = [] }
