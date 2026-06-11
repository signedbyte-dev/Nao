namespace Nao.Runtime.Orleans.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Core
open Nao.Loader
open Nao.Runtime.Orleans.Grains

/// A minimal LLM provider for testing — echoes input
type EchoProvider() =
    interface ILlmProvider with
        member _.Name = "echo"
        member _.CompleteAsync(conversation: Conversation) (_options: CompletionOptions) =
            let lastMsg =
                conversation
                |> List.tryLast
                |> Option.map (fun m -> m.Content)
                |> Option.defaultValue ""
            Task.FromResult({ Content = sprintf "echo: %s" lastMsg; FinishReason = "stop"; TokensUsed = Some 10 })

/// Helper to create workspace definitions for testing
module TestWorkspace =

    let agentDef name =
        { Name = name
          Description = sprintf "Test agent: %s" name
          Provider = "echo"
          Model = "test"
          Prompt = { Prompt.Empty with Role = "You are a test agent." }
          Tools = []
          SubAgents = []
          Options = CompletionOptions.Default
          MaxRounds = 1 }

    let toolDef name =
        { Name = name
          Description = sprintf "Test tool: %s" name
          Command = "echo"
          Args = ["test-output"] }

    let empty : WorkspaceDefinitions =
        { AgentDefs = []
          ToolDefs = []
          EvalSuiteDefs = []
          ConstitutionDefs = []
          Agents = []
          Tools = []
          Evaluators = []
          Errors = [] }

    let withAgent name (defs: WorkspaceDefinitions) : WorkspaceDefinitions =
        { defs with AgentDefs = defs.AgentDefs @ [agentDef name] }

    let withTool name (defs: WorkspaceDefinitions) : WorkspaceDefinitions =
        { defs with ToolDefs = defs.ToolDefs @ [toolDef name] }

    let withBuiltTool (tool: Tool) (defs: WorkspaceDefinitions) : WorkspaceDefinitions =
        { defs with Tools = defs.Tools @ [tool] }

[<TestClass>]
type GrainStateTests() =

    [<TestMethod>]
    member _.MessageRecord_RoundTrips() =
        let original = { Role = User; Content = "hello world" }
        let record = GrainStateMapping.fromMessage original
        let restored = GrainStateMapping.toMessage record
        Assert.AreEqual(original.Role, restored.Role)
        Assert.AreEqual(original.Content, restored.Content)

    [<TestMethod>]
    member _.MessageRecord_HandlesAllRoles() =
        for (role, roleStr) in [(User, "User"); (Assistant, "Assistant"); (System, "System")] do
            let msg = { Role = role; Content = "test" }
            let record = GrainStateMapping.fromMessage msg
            Assert.AreEqual(roleStr, record.Role)
            let restored = GrainStateMapping.toMessage record
            Assert.AreEqual(role, restored.Role)

    [<TestMethod>]
    member _.MemoryRecord_Converts() =
        let entry: MemoryEntry =
            { Key = "user-name"
              Value = "Alice"
              Timestamp = DateTimeOffset.UtcNow
              Tags = ["personal"; "identity"] }
        let record = MemoryRecord(Key = entry.Key, Value = entry.Value, Timestamp = entry.Timestamp, Tags = ResizeArray(entry.Tags))
        let roundtripped = GrainStateMapping.toMemoryEntry record
        Assert.AreEqual(entry.Key, roundtripped.Key)
        Assert.AreEqual(entry.Value, roundtripped.Value)
        Assert.AreEqual(entry.Tags, roundtripped.Tags)

[<TestClass>]
type SessionInfoTests() =

    [<TestMethod>]
    member _.SessionInfo_HasCorrectDefaults() =
        let info = SessionInfo()
        Assert.AreEqual("", info.AgentName)
        Assert.AreEqual("", info.SessionId)
        Assert.AreEqual("", info.UserId)
        Assert.AreEqual(true, info.IsActive)
        Assert.AreEqual(0, info.ToolNames.Count)

    [<TestMethod>]
    member _.SessionGrainState_StartsEmpty() =
        let state = SessionGrainState()
        Assert.AreEqual(0, state.ConversationHistory.Count)
        Assert.AreEqual(0, state.Memories.Count)
        Assert.AreEqual("", state.Info.AgentName)

[<TestClass>]
type SessionGrainKeyTests() =

    [<TestMethod>]
    member _.BuildKey_CombinesUserAndSession() =
        let key = SessionGrain.buildKey "user-1" "session-abc"
        Assert.AreEqual("user-1/session-abc", key)

    [<TestMethod>]
    member _.BuildKey_SupportsMultipleSessionsPerUser() =
        let k1 = SessionGrain.buildKey "user-1" "session-1"
        let k2 = SessionGrain.buildKey "user-1" "session-2"
        Assert.AreNotEqual(k1, k2)
        Assert.IsTrue(k1.StartsWith("user-1/"))
        Assert.IsTrue(k2.StartsWith("user-1/"))

[<TestClass>]
type WorkspaceResolutionTests() =

    [<TestMethod>]
    member _.EmptyWorkspace_HasNoAgents() =
        let ws = TestWorkspace.empty
        Assert.AreEqual(0, ws.AgentDefs.Length)
        Assert.AreEqual(0, ws.Agents.Length)

    [<TestMethod>]
    member _.WithAgent_AddsDefinition() =
        let ws = TestWorkspace.empty |> TestWorkspace.withAgent "test-agent"
        Assert.AreEqual(1, ws.AgentDefs.Length)
        Assert.AreEqual("test-agent", ws.AgentDefs.[0].Name)

    [<TestMethod>]
    member _.WithTool_AddsDefinition() =
        let ws = TestWorkspace.empty |> TestWorkspace.withTool "my-tool"
        Assert.AreEqual(1, ws.ToolDefs.Length)
        Assert.AreEqual("my-tool", ws.ToolDefs.[0].Name)

    [<TestMethod>]
    member _.DefinitionBuilder_BuildsAgentFromDef() =
        let provider = EchoProvider() :> ILlmProvider
        let def = TestWorkspace.agentDef "builder-test"
        let agent = DefinitionBuilder.buildAgent provider [] [] def
        // Orchestrator uses its own internal id name
        Assert.AreEqual("orchestrator", agent.Id.Name)

    [<TestMethod>]
    member _.DefinitionBuilder_AgentResponds() =
        let provider = EchoProvider() :> ILlmProvider
        let def = TestWorkspace.agentDef "echo-agent"
        let agent = DefinitionBuilder.buildAgent provider [] [] def
        let result = agent.RunAsync("hello").Result
        // Orchestrator sends the conversation to provider, gets echo back
        Assert.IsTrue(result.Length > 0)

    [<TestMethod>]
    member _.DefinitionBuilder_EachBuildIsIsolated() =
        let provider = EchoProvider() :> ILlmProvider
        let def = TestWorkspace.agentDef "isolated-agent"

        let a1 = DefinitionBuilder.buildAgent provider [] [] def
        let a2 = DefinitionBuilder.buildAgent provider [] [] def

        let _ = a1.RunAsync("msg1").Result
        Assert.IsTrue(a1.State.Conversation.Length > 0)
        Assert.AreEqual(0, a2.State.Conversation.Length)

    [<TestMethod>]
    member _.BuiltTool_ResolvedByName() =
        let myTool: Tool =
            { Name = "greet"
              Description = "greets"
              Execute = fun name -> Task.FromResult(sprintf "Hello %s" name) }
        let ws = TestWorkspace.empty |> TestWorkspace.withBuiltTool myTool
        let found = ws.Tools |> List.tryFind (fun t -> t.Name = "greet")
        Assert.IsTrue(found.IsSome)
        let result = found.Value.Execute("World").Result
        Assert.AreEqual("Hello World", result)

[<TestClass>]
type SessionDirectoryStateTests() =

    [<TestMethod>]
    member _.SessionEntry_HasCorrectDefaults() =
        let entry = SessionEntry()
        Assert.AreEqual("", entry.SessionId)
        Assert.AreEqual("", entry.AgentName)
        Assert.AreEqual(true, entry.IsActive)

    [<TestMethod>]
    member _.SessionDirectoryState_StartsEmpty() =
        let state = SessionDirectoryState()
        Assert.AreEqual(0, state.Sessions.Count)
