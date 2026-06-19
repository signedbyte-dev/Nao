namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Feedback
open Nao.Runtime.Orleans

// Allow the Orleans C# codegen project to access internal F# DU backing fields.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Nao.Runtime.Orleans.Codegen")>]
do ()

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
    /// Optional pinned agent version ("" = unversioned / latest).
    [<Id(10u)>] member val AgentVersion: string = "" with get, set
    /// Id of the most recently processed turn (used to attach feedback).
    [<Id(11u)>] member val LastTurnId: string = "" with get, set

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
    /// Optional pinned agent version ("" = unversioned / latest).
    [<Id(4u)>] member val AgentVersion: string = "" with get, set

/// Orleans grain interface for a user session.
/// Grain key format: "userId/sessionId"
/// The grain resolves agents/tools from the workspace registry.
type ISessionGrain =
    inherit IGrainWithStringKey

    /// Initialize the session with a specific agent, workspace, and optional tool overrides.
    abstract member StartAsync: options: SessionStartOptions -> Task<bool>

    /// Process user input — grain resolves workspace, builds agent, runs ETCLOVG harness.
    abstract member ProcessAsync: input: string -> Task<string>

    /// Id of the most recently processed turn (empty if none). Use to attach feedback.
    abstract member GetLastTurnIdAsync: unit -> Task<string>

    /// Submit feedback for the most recently processed turn. `sentiment` is
    /// "positive" / "negative" / "neutral". Returns the rationales of any tool
    /// adjustments that were proposed and stored.
    abstract member SubmitFeedbackAsync: sentiment: string * comment: string -> Task<string array>

    /// Switch to a different workspace (re-validates agent exists in new workspace)
    abstract member SwitchWorkspaceAsync: workspaceKey: string -> Task<bool>

    /// Switch to a different agent within the current workspace
    abstract member SwitchAgentAsync: agentName: string -> Task<bool>

    /// Create or switch to a named conversation context
    abstract member SwitchConversationAsync: conversationName: string -> Task

    /// List all conversation contexts in this session
    abstract member ListConversationsAsync: unit -> Task<string array>

    /// Get the session metadata
    abstract member GetInfoAsync: unit -> Task<SessionInfo>

    /// Get the current conversation history
    abstract member GetHistoryAsync: unit -> Task<MessageRecord array>

    /// Clear the current conversation context
    abstract member ClearHistoryAsync: unit -> Task

    /// Save a key-value memory entry within this session
    abstract member SaveMemoryAsync: key: string * value: string -> Task

    /// Recall memories by key prefix
    abstract member RecallMemoryAsync: key: string -> Task<MemoryEntry array>

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
        provider: ILlmProvider,
        orchestratorFactory: IOrchestratorFactory,
        conversationStore: IConversationStore,
        harnessServices: IHarnessServices,
        feedback: FeedbackService
    ) =
    inherit Grain()

    // ─── Workspace resolution ───

    let getWorkspace () : WorkspaceDefinitions option =
        let key = WorkspaceId.create persistentState.State.Info.WorkspaceKey
        registry.TryGet key

    let buildHarnessConfig (workspace: WorkspaceDefinitions) (tools: Tool list) (eventSink: IAgentEventSink) : EtclovgConfig =
        let constitution = DefinitionBuilder.buildMergedConstitution workspace.ConstitutionDefs
        let toolProtocol =
            if tools.Length > 0 then
                Some (ToolProtocol.fromTools tools)
            else
                None
        let baseConfig =
            { EtclovgConfig.Default with
                Constitution = constitution
                ToolProtocol = toolProtocol
                EventSink = eventSink }
        baseConfig.WithServices(harnessServices)

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

    let roleToString (role: Role) =
        match role with System -> "System" | User -> "User" | Assistant -> "Assistant"

    let stringToRole (s: string) =
        match s with "System" -> System | "Assistant" -> Assistant | _ -> User

    let restoreConversation () : Conversation =
        let ctx = activeConversation ()
        if ctx.Messages.Count > 0 then
            ctx.Messages
            |> Seq.map GrainStateMapping.toMessage
            |> Seq.toList
        else
            // Fallback: load from external conversation store
            let sessionId = persistentState.State.Info.SessionId
            let convName = persistentState.State.Info.ActiveConversation
            if not (String.IsNullOrEmpty sessionId) then
                let grainKey = sprintf "%s/%s" persistentState.State.Info.UserId sessionId
                let loaded = conversationStore.LoadAsync grainKey convName
                loaded.Result
                |> Array.map (fun m -> { Role = stringToRole m.Role; Content = m.Content })
                |> Array.toList
            else []

    let persistConversationAsync (conversation: Conversation) : Task =
        task {
            let ctx = activeConversation ()
            ctx.Messages.Clear()
            for msg in conversation do
                ctx.Messages.Add(GrainStateMapping.fromMessage msg)

            // Also persist to external store
            let sessionId = persistentState.State.Info.SessionId
            let userId = persistentState.State.Info.UserId
            let convName = persistentState.State.Info.ActiveConversation
            if not (String.IsNullOrEmpty sessionId) then
                let grainKey = sprintf "%s/%s" userId sessionId
                let messages =
                    conversation
                    |> List.map (fun m ->
                        { PersistedMessage.Role = roleToString m.Role
                          Content = m.Content
                          Timestamp = DateTimeOffset.UtcNow })
                    |> List.toArray
                do! conversationStore.SaveAsync grainKey convName messages
        }

    // ─── Tool resolution ───

    let resolveTool (workspace: WorkspaceDefinitions) (name: string) : Tool option =
        // Tool references may be version-qualified ("name@version").
        let (n, ver) = VersionRef.parse name
        workspace.Tools
        |> List.tryFind (fun t -> t.Name = n && VersionRef.matches ver t.Version)
        |> Option.orElseWith (fun () ->
            workspace.ToolDefs
            |> List.tryFind (fun d -> d.Name = n && VersionRef.matches ver d.Version)
            |> Option.map DefinitionBuilder.buildTool)

    let resolveTools (workspace: WorkspaceDefinitions) (names: string list) : Tool list =
        names |> List.choose (resolveTool workspace)

    // ─── Agent resolution ───

    /// Convert a stored version string ("" = none) into an optional version.
    let versionOpt (version: string) : string option =
        if String.IsNullOrEmpty version then None else Some version

    let findAgentDef (workspace: WorkspaceDefinitions) (name: string) (version: string option) : AgentDef option =
        workspace.AgentDefs
        |> List.tryFind (fun d -> d.Name = name && VersionRef.matches version d.Version)

    let findBuiltAgent (workspace: WorkspaceDefinitions) (name: string) (version: string option) : IAgent option =
        // Pre-built agents are unversioned (None); only resolvable when no version is requested.
        match version with
        | Some _ -> None
        | None -> workspace.Agents |> List.tryFind (fun a -> a.Id.Name = name)

    let agentExists (workspace: WorkspaceDefinitions) (name: string) (version: string option) : bool =
        (findAgentDef workspace name version).IsSome || (findBuiltAgent workspace name version).IsSome

    /// Build an agent, overlaying any active agent annotations onto the resolved
    /// definitions (the agent and its sub-agents) before construction. Annotations are
    /// runtime overlays — dropping them restores the legacy agent definition.
    let createAgentAsync (workspace: WorkspaceDefinitions) (name: string) (version: string option) (tools: Tool list) : Task<IAgent option> =
        task {
            match findAgentDef workspace name version with
            | Some def ->
                let! def = feedback.ApplyAgentAnnotationsAsync def
                let subAgents = ResizeArray<IAgent>()
                for subName in def.SubAgents do
                    let (subN, subVer) = VersionRef.parse subName
                    match findAgentDef workspace subN subVer with
                    | Some subDef ->
                        let! subDef = feedback.ApplyAgentAnnotationsAsync subDef
                        subAgents.Add(DefinitionBuilder.buildAgentWithFactory orchestratorFactory provider [] [] subDef)
                    | None ->
                        match findBuiltAgent workspace subN subVer with
                        | Some a -> subAgents.Add a
                        | None -> ()
                return Some (DefinitionBuilder.buildAgentWithFactory orchestratorFactory provider tools (List.ofSeq subAgents) def)
            | None ->
                return findBuiltAgent workspace name version
        }

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

                // Agent version may be specified explicitly or inline as "name@version".
                let (agentName, inlineVersion) = VersionRef.parse options.AgentName
                let agentVersion =
                    if String.IsNullOrEmpty options.AgentVersion then inlineVersion
                    else Some options.AgentVersion

                if not (agentExists workspace agentName agentVersion) then
                    return false
                else
                    let info = persistentState.State.Info
                    info.AgentName <- agentName
                    info.AgentVersion <- (agentVersion |> Option.defaultValue "")
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

                // Capture any implicit feedback the user expresses about the previous turn
                // (e.g. "that's wrong", "perfect, thanks"). It is persisted as conversation-
                // sourced feedback that feeds the cross-session suggestion pipeline, and noted
                // in session memory so the agent stays aware of it within this conversation.
                let priorTurnId = persistentState.State.Info.LastTurnId
                if not (String.IsNullOrEmpty priorTurnId) then
                    let info = persistentState.State.Info
                    match! feedback.CaptureImplicitFeedbackAsync(priorTurnId, info.SessionId, info.UserId, input) with
                    | Some fb ->
                        let note =
                            sprintf "%A feedback on turn %s: %s"
                                fb.Sentiment priorTurnId (fb.Comment |> Option.defaultValue "")
                        let record = MemoryRecord()
                        record.Key <- sprintf "feedback:%s" priorTurnId
                        record.Value <- note
                        record.Timestamp <- DateTimeOffset.UtcNow
                        match persistentState.State.Memories |> Seq.tryFindIndex (fun m -> m.Key = record.Key) with
                        | Some idx -> persistentState.State.Memories.[idx] <- record
                        | None -> persistentState.State.Memories.Add(record)
                    | None -> ()

                match getWorkspace () with
                | None ->
                    return sprintf "[Error] Workspace '%s' not available" persistentState.State.Info.WorkspaceKey
                | Some workspace ->

                let tools = resolveTools workspace (persistentState.State.Info.ToolNames |> Seq.toList)

                // Overlay any active feedback annotations so user-improved tool behaviour
                // takes effect transparently at load time (dropping them reverts to legacy).
                let! tools = feedback.ApplyToolAnnotationsAsync tools

                let agentVersion = versionOpt persistentState.State.Info.AgentVersion
                let! agentOpt = createAgentAsync workspace agentName agentVersion tools
                match agentOpt with
                | None ->
                    return sprintf "[Error] Agent '%s' not found in workspace '%s'" agentName persistentState.State.Info.WorkspaceKey
                | Some agent ->
                    let info = persistentState.State.Info
                    let turnId = Guid.NewGuid().ToString("N")
                    let recorder =
                        TurnRecorder.forTools tools
                            (turnId, info.SessionId, info.UserId, info.WorkspaceKey,
                             agentName, agentVersion, input)
                    let harnessConfig = buildHarnessConfig workspace tools (recorder :> IAgentEventSink)
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

                        do! persistConversationAsync finalConversation

                        // Record the turn so feedback can later be analysed against it.
                        let turnRecord = { recorder.Snapshot() with Output = response }
                        do! feedback.RecordTurnAsync turnRecord
                        info.LastTurnId <- turnId

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

        member _.GetLastTurnIdAsync() : Task<string> =
            Task.FromResult(persistentState.State.Info.LastTurnId)

        member _.SubmitFeedbackAsync(sentiment: string, comment: string) : Task<string array> =
            task {
                let info = persistentState.State.Info
                if String.IsNullOrEmpty info.LastTurnId then
                    return [||]
                else
                    let parsedSentiment =
                        match (sentiment |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
                        | "positive" | "up" | "good" -> FeedbackSentiment.Positive
                        | "negative" | "down" | "bad" -> FeedbackSentiment.Negative
                        | _ -> FeedbackSentiment.Neutral
                    let fb =
                        { Id = Guid.NewGuid()
                          TurnId = info.LastTurnId
                          SessionId = info.SessionId
                          UserId = info.UserId
                          Sentiment = parsedSentiment
                          Comment = if String.IsNullOrWhiteSpace comment then None else Some comment
                          CreatedAt = DateTimeOffset.UtcNow
                          Metadata = Map.empty }
                    let! proposals = feedback.SubmitFeedbackAsync fb
                    return proposals |> List.map (fun p -> p.Rationale) |> List.toArray
            }

        member _.SwitchWorkspaceAsync(workspaceKey: string) : Task<bool> =
            task {
                match registry.TryGet (WorkspaceId.create workspaceKey) with
                | None -> return false
                | Some workspace ->
                    let agentName = persistentState.State.Info.AgentName
                    let agentVersion = versionOpt persistentState.State.Info.AgentVersion
                    if not (String.IsNullOrEmpty(agentName)) && not (agentExists workspace agentName agentVersion) then
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
                    // The target agent may be version-qualified ("name@version").
                    let (targetName, targetVersion) = VersionRef.parse agentName
                    if not (agentExists workspace targetName targetVersion) then
                        return false
                    else
                        persistentState.State.Info.AgentName <- targetName
                        persistentState.State.Info.AgentVersion <- (targetVersion |> Option.defaultValue "")
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

        member _.ListConversationsAsync() : Task<string array> =
            persistentState.State.Conversations
            |> Seq.map (fun c -> c.Name)
            |> Seq.toArray
            |> Task.FromResult

        member _.GetInfoAsync() : Task<SessionInfo> =
            Task.FromResult(persistentState.State.Info)

        member _.GetHistoryAsync() : Task<MessageRecord array> =
            let ctx = activeConversation ()
            // Restore messages from persistent storage if they're not already loaded in memory
            if ctx.Messages.Count = 0 then
                let sessionId = persistentState.State.Info.SessionId
                let userId = persistentState.State.Info.UserId
                let convName = persistentState.State.Info.ActiveConversation
                if not (String.IsNullOrEmpty sessionId) then
                    let grainKey = sprintf "%s/%s" userId sessionId
                    let loaded = conversationStore.LoadAsync grainKey convName
                    for pm in loaded.Result do
                        let record = MessageRecord()
                        record.Role <- pm.Role
                        record.Content <- pm.Content
                        ctx.Messages.Add(record)
            ctx.Messages |> Seq.toArray |> Task.FromResult

        member this.ClearHistoryAsync() : Task =
            task {
                let ctx = activeConversation ()
                ctx.Messages.Clear()
                let grainKey = this.GetPrimaryKeyString()
                let convName = persistentState.State.Info.ActiveConversation
                do! conversationStore.DeleteConversationAsync grainKey convName
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

        member _.RecallMemoryAsync(key: string) : Task<MemoryEntry array> =
            persistentState.State.Memories
            |> Seq.filter (fun m -> m.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
            |> Seq.map GrainStateMapping.toMemoryEntry
            |> Seq.toArray
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
                // Clean up external conversation store
                let grainKey = this.GetPrimaryKeyString()
                do! conversationStore.DeleteSessionAsync(grainKey)
                do! persistentState.ClearStateAsync()
                this.DeactivateOnIdle()
            }

module SessionGrain =

    /// Build a grain key from userId and sessionId
    let buildKey (userId: string) (sessionId: string) =
        sprintf "%s/%s" userId sessionId
