namespace Nao.Agents.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Core
open Nao.Agents

/// Tests for OrchestratorBase.TryHandleDelegationAsync — the hook that lets a
/// subclass intercept delegation (e.g. hand it off to a background task) and
/// reply with a token instead of running the sub-agent in-process.
[<TestClass>]
type DelegationHookTests() =

    /// A provider that returns a fixed sequence of completions, one per round.
    let scriptedProvider (responses: string list) : ILlmProvider =
        let queue = System.Collections.Generic.Queue<string>(responses)
        { new ILlmProvider with
            member _.CompleteAsync _conversation _options =
                let content = if queue.Count > 0 then queue.Dequeue() else "done"
                Task.FromResult { Content = content; FinishReason = "stop"; TokensUsed = None }
            member _.Name = "scripted" }
    let makeAgent (name: string) (response: string) (invoked: bool ref) : IAgent =
        { new IAgent with
            member _.Id = { Name = name; Description = "test sub-agent" }
            member _.State = AgentState.Empty
            member _.RunAsync(_input) =
                invoked.Value <- true
                Task.FromResult response
            member _.HandleMessageAsync(_msg) = Task.FromResult None }

    let makeConfig (provider: ILlmProvider) (subAgents: IAgent list) : OrchestratorConfig =
        { Provider = provider
          Tools = []
          SubAgents = subAgents
          Prompt = Prompt.Empty
          Options = CompletionOptions.Default
          MaxRounds = 5
          EventSink = AgentEventSink.none
          Memory = OrchestratorMemoryConfig.None
          Instructions = None }

    let delegateJson (agent: string) (input: string) =
        sprintf "{\"action\":\"delegate\",\"name\":\"%s\",\"input\":\"%s\"}" agent input

    [<TestMethod>]
    member _.HandledDelegationReturnsTokenWithoutRunningSubAgent() =
        let invoked = ref false
        let agent = makeAgent "converter" "converted output" invoked
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md" ]
        let config = makeConfig provider [ agent ]
        let orchestrator =
            { new OrchestratorBase(config) with
                member _.TryHandleDelegationAsync(_agentName, _input) =
                    Task.FromResult(Some "task-token-123") }
        let result = (orchestrator :> IAgent).RunAsync("convert this file").Result
        Assert.AreEqual("task-token-123", result)
        Assert.IsFalse(invoked.Value, "Sub-agent must NOT run in-process when delegation is handled")

    [<TestMethod>]
    member _.UnhandledDelegationFallsBackToInProcessSubAgent() =
        let invoked = ref false
        let agent = makeAgent "converter" "converted output" invoked
        // Round 1: delegate. Round 2: final answer once the agent result is fed back.
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md"; "all done" ]
        let config = makeConfig provider [ agent ]
        // Default OrchestratorBase returns None from TryHandleDelegationAsync.
        let orchestrator = Orchestrator(config)
        let result = (orchestrator :> IAgent).RunAsync("convert this file").Result
        Assert.AreEqual("all done", result)
        Assert.IsTrue(invoked.Value, "Sub-agent should run in-process when delegation is not handled")
