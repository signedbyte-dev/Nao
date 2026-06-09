namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.Runtime

/// Persistent state for the session directory (per user)
[<GenerateSerializer>]
type SessionDirectoryState() =
    [<Id(0u)>]
    member val Sessions: ResizeArray<SessionEntry> = ResizeArray() with get, set

/// A single entry in the session directory
and [<GenerateSerializer>] SessionEntry() =
    [<Id(0u)>] member val SessionId: string = "" with get, set
    [<Id(1u)>] member val AgentName: string = "" with get, set
    [<Id(2u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(3u)>] member val IsActive: bool = true with get, set

/// Grain interface for managing a user's sessions.
/// Grain key: userId
/// Provides listing, creation, and removal of sessions for a single user.
type ISessionDirectoryGrain =
    inherit IGrainWithStringKey

    /// List all sessions for this user
    abstract member ListSessionsAsync: unit -> Task<SessionEntry list>

    /// List only active sessions
    abstract member ListActiveSessionsAsync: unit -> Task<SessionEntry list>

    /// Register a new session (called by SessionGrain on start)
    abstract member RegisterSessionAsync: sessionId: string * agentName: string -> Task

    /// Mark a session as paused
    abstract member MarkPausedAsync: sessionId: string -> Task

    /// Mark a session as active
    abstract member MarkActiveAsync: sessionId: string -> Task

    /// Remove a session from the directory (called by SessionGrain on destroy)
    abstract member RemoveSessionAsync: sessionId: string -> Task

/// Persistent directory grain that tracks all sessions for a user.
/// Grain key: userId
type SessionDirectoryGrain
    (
        [<PersistentState("sessionDirectory", "sessionStore")>] persistentState: IPersistentState<SessionDirectoryState>
    ) =
    inherit Grain()

    interface ISessionDirectoryGrain with
        member _.ListSessionsAsync() : Task<SessionEntry list> =
            persistentState.State.Sessions
            |> Seq.toList
            |> Task.FromResult

        member _.ListActiveSessionsAsync() : Task<SessionEntry list> =
            persistentState.State.Sessions
            |> Seq.filter (fun s -> s.IsActive)
            |> Seq.toList
            |> Task.FromResult

        member _.RegisterSessionAsync(sessionId: string, agentName: string) : Task =
            task {
                // Avoid duplicates
                let existing =
                    persistentState.State.Sessions
                    |> Seq.tryFindIndex (fun s -> s.SessionId = sessionId)
                match existing with
                | Some idx ->
                    persistentState.State.Sessions.[idx].AgentName <- agentName
                    persistentState.State.Sessions.[idx].IsActive <- true
                | None ->
                    let entry = SessionEntry()
                    entry.SessionId <- sessionId
                    entry.AgentName <- agentName
                    entry.CreatedAt <- DateTimeOffset.UtcNow
                    entry.IsActive <- true
                    persistentState.State.Sessions.Add(entry)
                do! persistentState.WriteStateAsync()
            }

        member _.MarkPausedAsync(sessionId: string) : Task =
            task {
                let entry =
                    persistentState.State.Sessions
                    |> Seq.tryFind (fun s -> s.SessionId = sessionId)
                match entry with
                | Some e ->
                    e.IsActive <- false
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

        member _.MarkActiveAsync(sessionId: string) : Task =
            task {
                let entry =
                    persistentState.State.Sessions
                    |> Seq.tryFind (fun s -> s.SessionId = sessionId)
                match entry with
                | Some e ->
                    e.IsActive <- true
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

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
