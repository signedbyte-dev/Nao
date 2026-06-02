namespace Nao.Providers

/// Identifies supported provider platforms
type ProviderType =
    | OpenAI of OpenAIConfig
    | Anthropic of AnthropicConfig
    | Vllm of VllmConfig
    | LlamaCpp of LlamaCppConfig
