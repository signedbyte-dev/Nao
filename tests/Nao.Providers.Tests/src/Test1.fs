namespace Nao.Providers.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Core
open Nao.Providers

[<TestClass>]
type ProviderFactoryTests () =

    [<TestMethod>]
    member _.CreatesOpenAIProvider () =
        let provider = ProviderFactory.create (OpenAI OpenAIConfig.Default)
        Assert.AreEqual("OpenAI", provider.Name)

    [<TestMethod>]
    member _.CreatesAnthropicProvider () =
        let provider = ProviderFactory.create (Anthropic AnthropicConfig.Default)
        Assert.AreEqual("Anthropic", provider.Name)

    [<TestMethod>]
    member _.CreatesVllmProvider () =
        let provider = ProviderFactory.create (Vllm VllmConfig.Default)
        Assert.AreEqual("vLLM", provider.Name)

    [<TestMethod>]
    member _.CreatesLlamaCppProvider () =
        let provider = ProviderFactory.create (LlamaCpp LlamaCppConfig.Default)
        Assert.AreEqual("llama.cpp", provider.Name)

    [<TestMethod>]
    member _.OpenAIProviderReturnsResult () =
        let provider = ProviderFactory.create (OpenAI OpenAIConfig.Default)
        let conversation = [ { Role = User; Content = "hi" } ]
        let result = (provider.CompleteAsync conversation CompletionOptions.Default).Result
        Assert.AreEqual("stop", result.FinishReason)

[<TestClass>]
type OpenAIConfigTests () =

    [<TestMethod>]
    member _.DefaultHasExpectedValues () =
        let config = OpenAIConfig.Default
        Assert.AreEqual("gpt-4", config.Model)
        Assert.AreEqual("https://api.openai.com/v1", config.BaseUrl)

[<TestClass>]
type AnthropicConfigTests () =

    [<TestMethod>]
    member _.DefaultHasExpectedValues () =
        let config = AnthropicConfig.Default
        Assert.AreEqual("claude-sonnet-4-20250514", config.Model)

[<TestClass>]
type VllmConfigTests () =

    [<TestMethod>]
    member _.DefaultUsesLocalhost () =
        let config = VllmConfig.Default
        Assert.AreEqual("http://localhost:8000/v1", config.BaseUrl)
        Assert.AreEqual(None, config.ApiKey)

[<TestClass>]
type LlamaCppConfigTests () =

    [<TestMethod>]
    member _.DefaultUsesLocalhost () =
        let config = LlamaCppConfig.Default
        Assert.AreEqual("http://localhost:8080", config.BaseUrl)
        Assert.AreEqual(None, config.NPredict)

