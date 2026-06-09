namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Nao.Core
open Nao.Agents
open Nao.Loader

/// Metadata about a session
[<GenerateSerializer>]
type SessionInfo() =
    [<Id(0u)>] member val AgentName: string = "" with get, set
    [<Id(1u)>] member val SessionId: string = "" with get, set
    [<Id(2u)>] member val UserId: string = "" with get, set
    [<Id(3u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(4u)>] member val LastActiveAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(5u)>] member val IsActive: bool = true with get, set
    [<Id(6u)>] member val ToolNames: ResizeArray<string> = ResizeArray() with get, set

/// Persistent state for a session grain
[<GenerateSerializer>]
type SessionGrainState() =
    [<Id(0u)>] member val Info: SessionInfo = SessionInfo() with get, set
    [<Id(1u)>] member val ConversationHistory: ResizeArray<MessageRecord> = ResizeArray() with get, set
    [<Id(2u)>] member val Memories: ResizeArray<MemoryRecord> = ResizeArray() with get, set

/// Orleans grain interface for a user session.
/// Grain key format: "userId/sessionId"
/// The grain owns all state and loads agents/tools from workspace definitions.
type ISessionGrain =
    inherit IGrainWithStringKey

    /// Initialize the session with a specific agent and optional tool overrides.
    abstract member StartAsync: agentName: string * toolNames: string list -> Task<bool>

    /// Process user input — grain builds a fresh agent, runs it, persists result.
    abstract member ProcessAsync: input: string -> Task<string>

    /// Get the session metadata
    abstract member GetInfoAsync: unit -> Task<SessionInfo>

    /// Get the conversation history
    abstract member GetHistoryAsync: unit -> Task<Message list>

    /// Clear conversation history but keep session alive
    abstract member ClearHistoryAsync: unit -> Task

    /// Save a key-value memory entry within this session
    abstract member SaveMemoryAsync: key: string * value: string -> Task

    /// Recall memories by key prefix
    abstract member RecallMemoryAsync: key: string -> Task<MemoryEntry list>

    /// Clear all memories
    abstract member ClearMemoriesAsync: unit -> Task

    /// Pause the session (marks inactive, keeps state)
    abstract member PauseAsync: unit -> Task

    /// Resume a paused session
    abstract member ResumeAsync: unit -> Task

    /// Permanently destroy the session and all its data
    abstract member DestroyAsync: unit -> Task

/// Self-contained session grain. Loads agents/tools from WorkspaceDefinitions,
/// manages conversation and memory state through Orleans persistence.
///
/// Dependencies (injected via Orleans DI at silo startup):
/// - WorkspaceDefinitions: loaded once by the host from .nao/ + plugins/
/// - ILlmProvider: the LLM backend used to power agents
///
/// On each ProcessAsync call:
/// 1. Finds the agent definition + tools from workspace
/// 2. Builds a fresh agent instance (no prior state)
/// 3. Runs the agent with user input
/// 4. Persists the updated conversation; agent instance is discarded
type SessionGrain
    (
        [<PersistentState("sessionState", "sessionStore")>] persistentState: IPersistentState<SessionGrainState>,
        workspace: WorkspaceDefinitions,
        provider: ILlmProvider
    ) =
    inherit Grain()

    let parseKey (key: string) =
        match key.IndexOf('/') with
        | -1 -> (key, "default")
        | idx -> (key.Substring(0, idx), key.Substring(idx + 1))

    let restoreConversation () : Conversation =
        persistentState.State.ConversationHistory
        |> Seq.map GrainStateMapping.toMessage
        |> Seq.toList

    let persistConversation (conversation: Conversation) =
        persistentState.State.ConversationHistory.Clear()
        for msg in conversation do
            persistentState.State.ConversationHistory.Add(GrainStateMapping.fromMessage msg)

    /// Resolve a tool by name from workspace definitions (both built tools and tool defs)
    let resolveTool (name: string) : Tool option =
        // First check pre-built tools (from assemblies)
        workspace.Tools
        |> List.tryFind (fun t -> t.Name = name)
        |> Option.orElseWith (fun () ->
            // Then check tool definitions (from JSON) and build on demand
            workspace.ToolDefs
            |> List.tryFind (fun d -> d.Name = name)
            |> Option.map DefinitionBuilder.buildTool)

    /// Resolve all tools requested for this session
    let resolveTools (names: string list) : Tool list =
        names |> List.choose resolveTool

    /// Find an agent definition by name
    let findAgentDef (name: string) : AgentDef option =
        workspace.AgentDefs |> List.tryFind (fun d -> d.Name = name)

    /// Find a pre-built agent by name (from assembly plugins)
    let findBuiltAgent (name: string) : IAgent option =
        workspace.Agents |> List.tryFind (fun a -> a.Id.Name = name)

    /// Create a fresh agent instance for the given name.
    /// Tries definition-based agents first, falls back to pre-built.
    let createAgent (name: string) (tools: Tool list) : IAgent option =
        match findAgentDef name with
        | Some def ->
            // Resolve sub-agents referenced by the definition
            let subAgents =
                def.SubAgents
                |> List.choose (fun subName ->
                    match findAgentDef subName with
                    | Some subDef -> Some (DefinitionBuilder.buildAgent provider [] [] subDef)
                    | None -> findBuiltAgent subName)
            Some (DefinitionBuilder.buildAgent provider tools subAgents def)
        | None ->
            findBuiltAgent name

    interface ISessionGrain with
        member this.StartAsync(agentName: string, toolNames: string list) : Task<bool> =
            task {
                let (userId, sessionId) = parseKey (this.GetPrimaryKeyString())

                // Verify the agent exists
                let agentExists =
                    (findAgentDef agentName).IsSome || (findBuiltAgent agentName).IsSome

                if not agentExists then
                    return false
                else
                    let info = persistentState.State.Info
                    info.AgentName <- agentName
                    info.SessionId <- sessionId
                    info.UserId <- userId
                    info.IsActive <- true
                    info.ToolNames <- ResizeArray(toolNames)
                    if info.CreatedAt = DateTimeOffset.MinValue then
                        info.CreatedAt <- DateTimeOffset.UtcNow
                    info.LastActiveAt <- DateTimeOffset.UtcNow
                    do! persistentState.WriteStateAsync()
                    return true
            }

        member _.ProcessAsync(input: string) : Task<string> =
            task {
                let agentName = persistentState.State.Info.AgentName
                if String.IsNullOrEmpty(agentName) then
                    return "[Error] Session not started. Call StartAsync first."
                elif not persistentState.State.Info.IsActive then
                    return "[Error] Session is paused. Call ResumeAsync first."
                else
                    let tools = resolveTools (persistentState.State.Info.ToolNames |> Seq.toList)

                    match createAgent agentName tools with
                    | None ->
                        return sprintf "[Error] Agent '%s' not found in workspace" agentName
                    | Some agent ->
                        let! result = agent.RunAsync(input)

                        // Extract conversation from the agent after processing
                        let updatedConversation = agent.State.Conversation
                        let existingConversation = restoreConversation ()

                        let finalConversation =
                            if updatedConversation.Length > 0 then
                                updatedConversation
                            else
                                existingConversation @ [
                                    { Role = User; Content = input }
                                    { Role = Assistant; Content = result }
                                ]

                        persistConversation finalConversation
                        persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                        do! persistentState.WriteStateAsync()
                        return result
            }

        member _.GetInfoAsync() : Task<SessionInfo> =
            Task.FromResult(persistentState.State.Info)

        member _.GetHistoryAsync() : Task<Message list> =
            restoreConversation () |> Task.FromResult

        member _.ClearHistoryAsync() : Task =
            task {
                persistentState.State.ConversationHistory.Clear()
                do! persistentState.WriteStateAsync()
            }

        member _.SaveMemoryAsync(key: string, value: string) : Task =
            task {
                let record = MemoryRecord()
                record.Key <- key
                record.Value <- value
                record.Timestamp <- DateTimeOffset.UtcNow
                let existing =
                    persistentState.State.Memories
                    |> Seq.tryFindIndex (fun m -> m.Key = key)
                match existing with
                | Some idx -> persistentState.State.Memories.[idx] <- record
                | None -> persistentState.State.Memories.Add(record)
                do! persistentState.WriteStateAsync()
            }

        member _.RecallMemoryAsync(key: string) : Task<MemoryEntry list> =
            persistentState.State.Memories
            |> Seq.filter (fun m -> m.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
            |> Seq.map GrainStateMapping.toMemoryEntry
            |> Seq.toList
            |> Task.FromResult

        member _.ClearMemoriesAsync() : Task =
            task {
                persistentState.State.Memories.Clear()
                do! persistentState.WriteStateAsync()
            }

        member _.PauseAsync() : Task =
            task {
                persistentState.State.Info.IsActive <- false
                do! persistentState.WriteStateAsync()
            }

        member _.ResumeAsync() : Task =
            task {
                persistentState.State.Info.IsActive <- true
                persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()
            }

        member this.DestroyAsync() : Task =
            task {
                do! persistentState.ClearStateAsync()
                this.DeactivateOnIdle()
            }

module SessionGrain =

    /// Build a grain key from userId and sessionId
    let buildKey (userId: string) (sessionId: string) =
        sprintf "%s/%s" userId sessionId
