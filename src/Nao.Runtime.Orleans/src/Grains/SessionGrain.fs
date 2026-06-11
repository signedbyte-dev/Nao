namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Runtime.Orleans

/// Metadata about a session
[<GenerateSerializer>]
type SessionInfo() =
    [<Id(0u)>] member val AgentName: string = "" with get, set
    [<Id(1u)>] member val SessionId: string = "" with get, set
    [<Id(2u)>] member val UserId: string = "" with get, set
    [<Id(3u)>] member val GroupId: string = "" with get, set
    [<Id(4u)>] member val WorkspaceKey: string = "default" with get, set
    [<Id(5u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(6u)>] member val LastActiveAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(7u)>] member val IsActive: bool = true with get, set
    [<Id(8u)>] member val ToolNames: ResizeArray<string> = ResizeArray() with get, set
    [<Id(9u)>] member val ActiveConversation: string = "default" with get, set

/// A named conversation context within a session
[<GenerateSerializer>]
type ConversationContext() =
    [<Id(0u)>] member val Name: string = "default" with get, set
    [<Id(1u)>] member val Messages: ResizeArray<MessageRecord> = ResizeArray() with get, set
    [<Id(2u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(3u)>] member val AgentName: string = "" with get, set

/// Persistent state for a session grain
[<GenerateSerializer>]
type SessionGrainState() =
    [<Id(0u)>] member val Info: SessionInfo = SessionInfo() with get, set
    [<Id(1u)>] member val Conversations: ResizeArray<ConversationContext> = ResizeArray() with get, set
    [<Id(2u)>] member val Memories: ResizeArray<MemoryRecord> = ResizeArray() with get, set

/// Options for starting or reconfiguring a session
[<GenerateSerializer>]
type SessionStartOptions() =
    [<Id(0u)>] member val AgentName: string = "" with get, set
    [<Id(1u)>] member val ToolNames: ResizeArray<string> = ResizeArray() with get, set
    [<Id(2u)>] member val WorkspaceKey: string = "default" with get, set
    [<Id(3u)>] member val GroupId: string = "" with get, set

/// Orleans grain interface for a user session.
/// Grain key format: "userId/sessionId"
/// The grain resolves agents/tools from the workspace registry.
type ISessionGrain =
    inherit IGrainWithStringKey

    /// Initialize the session with a specific agent, workspace, and optional tool overrides.
    abstract member StartAsync: options: SessionStartOptions -> Task<bool>

    /// Process user input — grain resolves workspace, builds agent, runs ETCLOVG harness.
    abstract member ProcessAsync: input: string -> Task<string>

    /// Switch to a different workspace (re-validates agent exists in new workspace)
    abstract member SwitchWorkspaceAsync: workspaceKey: string -> Task<bool>

    /// Switch to a different agent within the current workspace
    abstract member SwitchAgentAsync: agentName: string -> Task<bool>

    /// Create or switch to a named conversation context
    abstract member SwitchConversationAsync: conversationName: string -> Task

    /// List all conversation contexts in this session
    abstract member ListConversationsAsync: unit -> Task<string list>

    /// Get the session metadata
    abstract member GetInfoAsync: unit -> Task<SessionInfo>

    /// Get the current conversation history
    abstract member GetHistoryAsync: unit -> Task<Message list>

    /// Clear the current conversation context
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

/// Self-contained session grain. Resolves workspaces from IWorkspaceRegistry,
/// manages multiple conversation contexts and memory through Orleans persistence.
///
/// Dependencies (injected via Orleans DI at silo startup):
/// - IWorkspaceRegistry: multi-tenant workspace registry (multiple workspaces per silo)
/// - ILlmProvider: the LLM backend used to power agents
///
/// On each ProcessAsync call:
/// 1. Resolves the workspace from registry by stored key
/// 2. Finds the agent definition + tools from that workspace
/// 3. Builds a fresh agent instance (no prior state)
/// 4. Runs the agent through the full ETCLOVG harness (governance, verification, etc.)
/// 5. Persists the updated conversation in the active context
type SessionGrain
    (
        [<PersistentState("sessionState", "sessionStore")>] persistentState: IPersistentState<SessionGrainState>,
        registry: IWorkspaceRegistry,
        provider: ILlmProvider
    ) =
    inherit Grain()

    // ─── Workspace resolution ───

    let getWorkspace () : WorkspaceDefinitions option =
        let key = WorkspaceId.create persistentState.State.Info.WorkspaceKey
        registry.TryGet key

    let buildHarnessConfig (workspace: WorkspaceDefinitions) (tools: Tool list) : EtclovgConfig =
        let constitution = DefinitionBuilder.buildMergedConstitution workspace.ConstitutionDefs
        let toolProtocol =
            if tools.Length > 0 then
                Some (ToolProtocol.fromTools tools)
            else
                None
        { EtclovgConfig.Default with
            Constitution = constitution
            ToolProtocol = toolProtocol
            Metrics = Some (InMemoryMetricsCollector() :> IMetricsCollector) }

    // ─── Key parsing ───

    let parseKey (key: string) =
        match key.IndexOf('/') with
        | -1 -> (key, "default")
        | idx -> (key.Substring(0, idx), key.Substring(idx + 1))

    // ─── Conversation context management ───

    let getOrCreateConversation (name: string) : ConversationContext =
        let existing =
            persistentState.State.Conversations
            |> Seq.tryFind (fun c -> c.Name = name)
        match existing with
        | Some ctx -> ctx
        | None ->
            let ctx = ConversationContext()
            ctx.Name <- name
            ctx.CreatedAt <- DateTimeOffset.UtcNow
            ctx.AgentName <- persistentState.State.Info.AgentName
            persistentState.State.Conversations.Add(ctx)
            ctx

    let activeConversation () : ConversationContext =
        getOrCreateConversation persistentState.State.Info.ActiveConversation

    let restoreConversation () : Conversation =
        let ctx = activeConversation ()
        ctx.Messages
        |> Seq.map GrainStateMapping.toMessage
        |> Seq.toList

    let persistConversation (conversation: Conversation) =
        let ctx = activeConversation ()
        ctx.Messages.Clear()
        for msg in conversation do
            ctx.Messages.Add(GrainStateMapping.fromMessage msg)

    // ─── Tool resolution ───

    let resolveTool (workspace: WorkspaceDefinitions) (name: string) : Tool option =
        workspace.Tools
        |> List.tryFind (fun t -> t.Name = name)
        |> Option.orElseWith (fun () ->
            workspace.ToolDefs
            |> List.tryFind (fun d -> d.Name = name)
            |> Option.map DefinitionBuilder.buildTool)

    let resolveTools (workspace: WorkspaceDefinitions) (names: string list) : Tool list =
        names |> List.choose (resolveTool workspace)

    // ─── Agent resolution ───

    let findAgentDef (workspace: WorkspaceDefinitions) (name: string) : AgentDef option =
        workspace.AgentDefs |> List.tryFind (fun d -> d.Name = name)

    let findBuiltAgent (workspace: WorkspaceDefinitions) (name: string) : IAgent option =
        workspace.Agents |> List.tryFind (fun a -> a.Id.Name = name)

    let agentExists (workspace: WorkspaceDefinitions) (name: string) : bool =
        (findAgentDef workspace name).IsSome || (findBuiltAgent workspace name).IsSome

    let createAgent (workspace: WorkspaceDefinitions) (name: string) (tools: Tool list) : IAgent option =
        match findAgentDef workspace name with
        | Some def ->
            let subAgents =
                def.SubAgents
                |> List.choose (fun subName ->
                    match findAgentDef workspace subName with
                    | Some subDef -> Some (DefinitionBuilder.buildAgent provider [] [] subDef)
                    | None -> findBuiltAgent workspace subName)
            Some (DefinitionBuilder.buildAgent provider tools subAgents def)
        | None ->
            findBuiltAgent workspace name

    // ─── Interface implementation ───

    interface ISessionGrain with
        member this.StartAsync(options: SessionStartOptions) : Task<bool> =
            task {
                let (userId, sessionId) = parseKey (this.GetPrimaryKeyString())

                let workspaceKey =
                    if String.IsNullOrEmpty(options.WorkspaceKey) then "default"
                    else options.WorkspaceKey

                match registry.TryGet (WorkspaceId.create workspaceKey) with
                | None -> return false
                | Some workspace ->

                if not (agentExists workspace options.AgentName) then
                    return false
                else
                    let info = persistentState.State.Info
                    info.AgentName <- options.AgentName
                    info.SessionId <- sessionId
                    info.UserId <- userId
                    info.GroupId <- options.GroupId
                    info.WorkspaceKey <- workspaceKey
                    info.IsActive <- true
                    info.ToolNames <- ResizeArray(options.ToolNames)
                    info.ActiveConversation <- "default"
                    if info.CreatedAt = DateTimeOffset.MinValue then
                        info.CreatedAt <- DateTimeOffset.UtcNow
                    info.LastActiveAt <- DateTimeOffset.UtcNow

                    // Ensure default conversation exists
                    getOrCreateConversation "default" |> ignore

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

                match getWorkspace () with
                | None ->
                    return sprintf "[Error] Workspace '%s' not available" persistentState.State.Info.WorkspaceKey
                | Some workspace ->

                let tools = resolveTools workspace (persistentState.State.Info.ToolNames |> Seq.toList)

                match createAgent workspace agentName tools with
                | None ->
                    return sprintf "[Error] Agent '%s' not found in workspace '%s'" agentName persistentState.State.Info.WorkspaceKey
                | Some agent ->
                    let harnessConfig = buildHarnessConfig workspace tools
                    let! result = EtclovgHarness.runAsync harnessConfig agent input

                    match result.Success, result.Response with
                    | true, Some response ->
                        let updatedConversation = agent.State.Conversation
                        let existingConversation = restoreConversation ()

                        let finalConversation =
                            if updatedConversation.Length > 0 then
                                updatedConversation
                            else
                                existingConversation @ [
                                    { Role = User; Content = input }
                                    { Role = Assistant; Content = response }
                                ]

                        persistConversation finalConversation
                        persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                        do! persistentState.WriteStateAsync()
                        return response

                    | _ ->
                        let errorMsg =
                            match result.HarnessError with
                            | Some err -> sprintf "[Blocked] %s" err.Message
                            | None -> result.Error |> Option.defaultValue "[Error] Unknown harness failure"
                        return errorMsg
            }

        member _.SwitchWorkspaceAsync(workspaceKey: string) : Task<bool> =
            task {
                match registry.TryGet (WorkspaceId.create workspaceKey) with
                | None -> return false
                | Some workspace ->
                    let agentName = persistentState.State.Info.AgentName
                    if not (String.IsNullOrEmpty(agentName)) && not (agentExists workspace agentName) then
                        return false
                    else
                        persistentState.State.Info.WorkspaceKey <- workspaceKey
                        persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                        do! persistentState.WriteStateAsync()
                        return true
            }

        member _.SwitchAgentAsync(agentName: string) : Task<bool> =
            task {
                match getWorkspace () with
                | None -> return false
                | Some workspace ->
                    if not (agentExists workspace agentName) then
                        return false
                    else
                        persistentState.State.Info.AgentName <- agentName
                        persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                        do! persistentState.WriteStateAsync()
                        return true
            }

        member _.SwitchConversationAsync(conversationName: string) : Task =
            task {
                persistentState.State.Info.ActiveConversation <- conversationName
                getOrCreateConversation conversationName |> ignore
                persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()
            }

        member _.ListConversationsAsync() : Task<string list> =
            persistentState.State.Conversations
            |> Seq.map (fun c -> c.Name)
            |> Seq.toList
            |> Task.FromResult

        member _.GetInfoAsync() : Task<SessionInfo> =
            Task.FromResult(persistentState.State.Info)

        member _.GetHistoryAsync() : Task<Message list> =
            restoreConversation () |> Task.FromResult

        member _.ClearHistoryAsync() : Task =
            task {
                let ctx = activeConversation ()
                ctx.Messages.Clear()
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
