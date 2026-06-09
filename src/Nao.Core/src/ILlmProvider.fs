namespace Nao.Core

open System.Threading.Tasks

/// Abstract interface for LLM providers.
/// Implementations wrap specific backends (Ollama, OpenAI, Anthropic, etc.)
type ILlmProvider =
    /// Send a conversation to the LLM and return the completion result
    abstract member CompleteAsync: Conversation -> CompletionOptions -> Task<CompletionResult>
    /// Human-readable name identifying this provider instance (e.g. "ollama", "openai")
    abstract member Name: string
