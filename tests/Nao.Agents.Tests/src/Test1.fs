namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Core

[<TestClass>]
type PromptTests () =

    [<TestMethod>]
    member _.EmptyPromptRendersEmpty () =
        let result = Prompt.render Prompt.Empty
        Assert.AreEqual("", result)

    [<TestMethod>]
    member _.RenderIncludesRole () =
        let prompt = { Prompt.Empty with Role = "You are a helpful assistant" }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Role"))
        Assert.IsTrue(result.Contains("You are a helpful assistant"))

    [<TestMethod>]
    member _.RenderIncludesObjective () =
        let prompt = { Prompt.Empty with Objective = "Summarize text" }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Objective"))
        Assert.IsTrue(result.Contains("Summarize text"))

    [<TestMethod>]
    member _.RenderIncludesDomainKnowledge () =
        let prompt = { Prompt.Empty with DomainKnowledge = ["Fact 1"; "Fact 2"] }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Domain Knowledge"))
        Assert.IsTrue(result.Contains("- Fact 1"))
        Assert.IsTrue(result.Contains("- Fact 2"))

    [<TestMethod>]
    member _.RenderIncludesConstraints () =
        let prompt = { Prompt.Empty with Constraints = ["Be concise"; "No speculation"] }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Constraints"))
        Assert.IsTrue(result.Contains("- Be concise"))

    [<TestMethod>]
    member _.RenderIncludesExamples () =
        let example = { Input = "Hello"; Output = "Hi there"; Explanation = Some "Greeting" }
        let prompt = { Prompt.Empty with Examples = [example] }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Examples"))
        Assert.IsTrue(result.Contains("Input: Hello"))
        Assert.IsTrue(result.Contains("Output: Hi there"))
        Assert.IsTrue(result.Contains("Explanation: Greeting"))

    [<TestMethod>]
    member _.RenderJsonOutputFormat () =
        let prompt = { Prompt.Empty with OutputFormat = Json(Some """{"type":"object"}""") }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("Respond in JSON."))
        Assert.IsTrue(result.Contains("Schema:"))

    [<TestMethod>]
    member _.RenderMarkdownOutputFormat () =
        let prompt = { Prompt.Empty with OutputFormat = Markdown }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("Respond in Markdown."))

    [<TestMethod>]
    member _.RenderContextSection () =
        let prompt = { Prompt.Empty with Context = ["Document A"; "Document B"] }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Context"))
        Assert.IsTrue(result.Contains("- Document A"))

[<TestClass>]
type AgentMessageTests () =

    [<TestMethod>]
    member _.CreateDirectedMessage () =
        let from = { Name = "agent1"; Description = "" }
        let toAgent = { Name = "agent2"; Description = "" }
        let msg = AgentMessage.create from toAgent "hello"
        Assert.AreEqual("agent1", msg.From.Name)
        Assert.AreEqual(Some toAgent, msg.To)
        Assert.AreEqual("hello", msg.Content)

    [<TestMethod>]
    member _.BroadcastHasNoRecipient () =
        let from = { Name = "agent1"; Description = "" }
        let msg = AgentMessage.broadcast from "hello all"
        Assert.AreEqual(None, msg.To)
        Assert.AreEqual("hello all", msg.Content)

[<TestClass>]
type AgentLoggerTests () =

    [<TestMethod>]
    member _.CollectLoggerCapturesEntries () =
        let entries = ResizeArray<LogEntry>()
        let logger = AgentLogger.collect entries "test-source"
        logger.Log Info "test message"
        Assert.AreEqual(1, entries.Count)
        Assert.AreEqual(Info, entries.[0].Level)
        Assert.AreEqual("test-source", entries.[0].Source)
        Assert.AreEqual("test message", entries.[0].Message)

    [<TestMethod>]
    member _.CollectLoggerCapturesData () =
        let entries = ResizeArray<LogEntry>()
        let logger = AgentLogger.collect entries "src"
        let data = Map.ofList [("key", box "value")]
        logger.LogWith Debug "msg" data
        Assert.AreEqual(1, entries.Count)
        Assert.AreEqual(Debug, entries.[0].Level)
        Assert.IsTrue(entries.[0].Data.ContainsKey("key"))

    [<TestMethod>]
    member _.SilentLoggerDoesNotThrow () =
        let logger = AgentLogger.silent
        logger.Log Error "should not crash"
        logger.LogWith Warning "also fine" Map.empty
        Assert.IsTrue(true)

[<TestClass>]
type AgentStateTests () =

    [<TestMethod>]
    member _.EmptyStateHasDefaults () =
        let state = AgentState.Empty
        Assert.AreEqual([], state.Conversation)
        Assert.AreEqual(Map.empty, state.Memory)

