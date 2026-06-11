namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.Runtime

/// An entry in the group directory (tracks session ownership within a group)
[<GenerateSerializer>]
type GroupSessionEntry() =
    [<Id(0u)>] member val UserId: string = "" with get, set
    [<Id(1u)>] member val SessionId: string = "" with get, set
    [<Id(2u)>] member val AgentName: string = "" with get, set
    [<Id(3u)>] member val WorkspaceKey: string = "default" with get, set
    [<Id(4u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(5u)>] member val IsActive: bool = true with get, set

/// Group membership entry
[<GenerateSerializer>]
type GroupMember() =
    [<Id(0u)>] member val UserId: string = "" with get, set
    [<Id(1u)>] member val Role: string = "member" with get, set
    [<Id(2u)>] member val JoinedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

/// Persistent state for the group directory grain
[<GenerateSerializer>]
type GroupDirectoryState() =
    [<Id(0u)>] member val GroupName: string = "" with get, set
    [<Id(1u)>] member val Members: ResizeArray<GroupMember> = ResizeArray() with get, set
    [<Id(2u)>] member val Sessions: ResizeArray<GroupSessionEntry> = ResizeArray() with get, set
    [<Id(3u)>] member val DefaultWorkspaceKey: string = "default" with get, set
    [<Id(4u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

/// Grain interface for managing sessions by group.
/// Grain key: groupId
/// Allows organizations to manage shared agents, workspaces, and sessions.
type IGroupDirectoryGrain =
    inherit IGrainWithStringKey

    /// Initialize the group with a name and default workspace
    abstract member InitAsync: groupName: string * defaultWorkspaceKey: string -> Task

    /// Add a member to the group
    abstract member AddMemberAsync: userId: string * role: string -> Task

    /// Remove a member from the group
    abstract member RemoveMemberAsync: userId: string -> Task<bool>

    /// List all group members
    abstract member ListMembersAsync: unit -> Task<GroupMember list>

    /// Check if a user is a member of this group
    abstract member IsMemberAsync: userId: string -> Task<bool>

    /// Register a session under this group
    abstract member RegisterSessionAsync: userId: string * sessionId: string * agentName: string * workspaceKey: string -> Task

    /// List all sessions in the group
    abstract member ListSessionsAsync: unit -> Task<GroupSessionEntry list>

    /// List sessions for a specific user within the group
    abstract member ListUserSessionsAsync: userId: string -> Task<GroupSessionEntry list>

    /// Remove a session from the group
    abstract member RemoveSessionAsync: sessionId: string -> Task

    /// Set the default workspace for all new sessions in this group
    abstract member SetDefaultWorkspaceAsync: workspaceKey: string -> Task

    /// Get the group's default workspace key
    abstract member GetDefaultWorkspaceAsync: unit -> Task<string>

/// Persistent grain that manages sessions within a group/organization.
/// Grain key: groupId
type GroupDirectoryGrain
    (
        [<PersistentState("groupDirectory", "sessionStore")>] persistentState: IPersistentState<GroupDirectoryState>
    ) =
    inherit Grain()

    interface IGroupDirectoryGrain with
        member _.InitAsync(groupName: string, defaultWorkspaceKey: string) : Task =
            task {
                let state = persistentState.State
                if state.CreatedAt = DateTimeOffset.MinValue then
                    state.GroupName <- groupName
                    state.DefaultWorkspaceKey <- defaultWorkspaceKey
                    state.CreatedAt <- DateTimeOffset.UtcNow
                    do! persistentState.WriteStateAsync()
            }

        member _.AddMemberAsync(userId: string, role: string) : Task =
            task {
                let existing =
                    persistentState.State.Members
                    |> Seq.tryFindIndex (fun m -> m.UserId = userId)
                match existing with
                | Some idx ->
                    persistentState.State.Members.[idx].Role <- role
                | None ->
                    let m = GroupMember()
                    m.UserId <- userId
                    m.Role <- role
                    m.JoinedAt <- DateTimeOffset.UtcNow
                    persistentState.State.Members.Add(m)
                do! persistentState.WriteStateAsync()
            }

        member _.RemoveMemberAsync(userId: string) : Task<bool> =
            task {
                let idx =
                    persistentState.State.Members
                    |> Seq.tryFindIndex (fun m -> m.UserId = userId)
                match idx with
                | Some i ->
                    persistentState.State.Members.RemoveAt(i)
                    do! persistentState.WriteStateAsync()
                    return true
                | None ->
                    return false
            }

        member _.ListMembersAsync() : Task<GroupMember list> =
            persistentState.State.Members
            |> Seq.toList
            |> Task.FromResult

        member _.IsMemberAsync(userId: string) : Task<bool> =
            persistentState.State.Members
            |> Seq.exists (fun m -> m.UserId = userId)
            |> Task.FromResult

        member _.RegisterSessionAsync(userId: string, sessionId: string, agentName: string, workspaceKey: string) : Task =
            task {
                let existing =
                    persistentState.State.Sessions
                    |> Seq.tryFindIndex (fun s -> s.SessionId = sessionId)
                match existing with
                | Some idx ->
                    let entry = persistentState.State.Sessions.[idx]
                    entry.AgentName <- agentName
                    entry.WorkspaceKey <- workspaceKey
                    entry.IsActive <- true
                | None ->
                    let entry = GroupSessionEntry()
                    entry.UserId <- userId
                    entry.SessionId <- sessionId
                    entry.AgentName <- agentName
                    entry.WorkspaceKey <- workspaceKey
                    entry.CreatedAt <- DateTimeOffset.UtcNow
                    entry.IsActive <- true
                    persistentState.State.Sessions.Add(entry)
                do! persistentState.WriteStateAsync()
            }

        member _.ListSessionsAsync() : Task<GroupSessionEntry list> =
            persistentState.State.Sessions
            |> Seq.toList
            |> Task.FromResult

        member _.ListUserSessionsAsync(userId: string) : Task<GroupSessionEntry list> =
            persistentState.State.Sessions
            |> Seq.filter (fun s -> s.UserId = userId)
            |> Seq.toList
            |> Task.FromResult

        member _.RemoveSessionAsync(sessionId: string) : Task =
            task {
                let idx =
                    persistentState.State.Sessions
                    |> Seq.tryFindIndex (fun s -> s.SessionId = sessionId)
                match idx with
                | Some i ->
                    persistentState.State.Sessions.RemoveAt(i)
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

        member _.SetDefaultWorkspaceAsync(workspaceKey: string) : Task =
            task {
                persistentState.State.DefaultWorkspaceKey <- workspaceKey
                do! persistentState.WriteStateAsync()
            }

        member _.GetDefaultWorkspaceAsync() : Task<string> =
            Task.FromResult(persistentState.State.DefaultWorkspaceKey)
