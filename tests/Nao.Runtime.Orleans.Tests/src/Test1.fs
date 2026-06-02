namespace Nao.Runtime.Orleans.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans.Grains

/// A test agent implementation for verifying grain behavior
type TestAgent(name: string) =
    let id = { Name = name; Description = "test agent" }
    let mutable state = AgentState.Empty

    interface IAgent with
        member _.Id = id
        member _.State = state
        member _.RunAsync(input: string) =
            Task.FromResult(sprintf "echo: %s" input)
        member _.HandleMessageAsync(msg: AgentMessage) =
            let reply = AgentMessage.create id msg.From (sprintf "reply to: %s" msg.Content)
            Task.FromResult(Some reply)

/// Concrete grain using the test agent for unit testing
type TestAgentGrain() =
    inherit AgentGrainBase()
    override _.Agent = TestAgent("test-grain") :> IAgent

[<TestClass>]
type AgentGrainBaseTests () =

    [<TestMethod>]
    member _.ProcessAsyncDelegatesToAgent () =
        let grain = TestAgentGrain() :> IAgentGrain
        let result = grain.ProcessAsync("hello").Result
        Assert.AreEqual("echo: hello", result)

    [<TestMethod>]
    member _.GetAgentIdReturnsName () =
        let grain = TestAgentGrain() :> IAgentGrain
        let name = grain.GetAgentIdAsync().Result
        Assert.AreEqual("test-grain", name)

    [<TestMethod>]
    member _.ReceiveMessageReturnsReply () =
        let grain = TestAgentGrain() :> IAgentGrain
        let reply = (grain.ReceiveMessageAsync "sender-agent" "hi there").Result
        Assert.IsTrue(reply.IsSome)
        Assert.IsTrue(reply.Value.Contains("reply to: hi there"))

