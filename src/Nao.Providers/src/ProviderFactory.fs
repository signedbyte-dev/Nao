namespace Nao.Providers

open System.Threading.Tasks
open Nao.Core

/// Factory for creating LLM providers
module ProviderFactory =

    let create (providerType: ProviderType) : ILlmProvider =
        match providerType with
        | OpenAI _config ->
            { new ILlmProvider with
                member _.Name = "OpenAI"
                member _.CompleteAsync _conversation _options =
                    Task.FromResult { Content = ""; FinishReason = "stop"; TokensUsed = None } }
        | Anthropic _config ->
            { new ILlmProvider with
                member _.Name = "Anthropic"
                member _.CompleteAsync _conversation _options =
                    Task.FromResult { Content = ""; FinishReason = "stop"; TokensUsed = None } }
        | Vllm _config ->
            { new ILlmProvider with
                member _.Name = "vLLM"
                member _.CompleteAsync _conversation _options =
                    Task.FromResult { Content = ""; FinishReason = "stop"; TokensUsed = None } }
        | LlamaCpp _config ->
            { new ILlmProvider with
                member _.Name = "llama.cpp"
                member _.CompleteAsync _conversation _options =
                    Task.FromResult { Content = ""; FinishReason = "stop"; TokensUsed = None } }
