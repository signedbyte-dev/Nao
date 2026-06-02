namespace Nao.Core

open System.Threading.Tasks

/// Abstract interface for LLM providers
type ILlmProvider =
    abstract member CompleteAsync: Conversation -> CompletionOptions -> Task<CompletionResult>
    abstract member Name: string
