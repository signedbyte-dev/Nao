namespace Nao.Runtime.Orleans

open System.Threading.Tasks
open Nao.Loader

/// Identifies a loaded workspace
type WorkspaceId = { Key: string }

module WorkspaceId =
    let create (key: string) = { Key = key }
    let defaultId = { Key = "default" }

/// Registry that manages multiple workspaces within a single silo.
/// Customers register workspaces at silo startup or dynamically at runtime.
/// Grains resolve workspace definitions by key on each request.
type IWorkspaceRegistry =
    /// Get a workspace by key. Returns None if not registered.
    abstract member TryGet: WorkspaceId -> WorkspaceDefinitions option
    /// Get a workspace by key, throwing if not found.
    abstract member Get: WorkspaceId -> WorkspaceDefinitions
    /// List all registered workspace keys.
    abstract member ListKeys: unit -> WorkspaceId list
    /// Register or update a workspace. Thread-safe.
    abstract member Register: WorkspaceId * WorkspaceDefinitions -> unit
    /// Remove a workspace from the registry.
    abstract member Remove: WorkspaceId -> bool
    /// Reload a workspace from its original source path.
    abstract member ReloadAsync: WorkspaceId -> Task<bool>

/// In-memory workspace registry backed by a concurrent dictionary.
/// Designed to be registered as a singleton in the Orleans DI container.
type WorkspaceRegistry() =
    let workspaces = System.Collections.Concurrent.ConcurrentDictionary<string, WorkspaceDefinitions * string option>()

    /// Register a workspace with an optional source path for reload support
    member _.Register(id: WorkspaceId, defs: WorkspaceDefinitions, ?sourcePath: string) =
        workspaces.[id.Key] <- (defs, sourcePath)

    interface IWorkspaceRegistry with
        member _.TryGet(id: WorkspaceId) =
            match workspaces.TryGetValue(id.Key) with
            | true, (defs, _) -> Some defs
            | _ -> None

        member this.Get(id: WorkspaceId) =
            match (this :> IWorkspaceRegistry).TryGet(id) with
            | Some defs -> defs
            | None -> failwithf "Workspace '%s' not registered" id.Key

        member _.ListKeys() =
            workspaces.Keys |> Seq.map WorkspaceId.create |> Seq.toList

        member _.Register(id: WorkspaceId, defs: WorkspaceDefinitions) =
            workspaces.[id.Key] <- (defs, None)

        member _.Remove(id: WorkspaceId) =
            workspaces.TryRemove(id.Key) |> fst

        member _.ReloadAsync(id: WorkspaceId) =
            task {
                match workspaces.TryGetValue(id.Key) with
                | true, (_, Some path) ->
                    let reloaded = WorkspaceLoader.loadWorkspace path
                    workspaces.[id.Key] <- (reloaded, Some path)
                    return true
                | _ ->
                    return false
            }

module WorkspaceRegistry =

    /// Create a registry and register a default workspace from a path
    let fromWorkspace (path: string) : WorkspaceRegistry =
        let reg = WorkspaceRegistry()
        let defs = WorkspaceLoader.loadWorkspace path
        reg.Register(WorkspaceId.defaultId, defs, sourcePath = path)
        reg

    /// Create a registry from multiple named workspace paths
    let fromWorkspaces (workspaces: (string * string) list) : WorkspaceRegistry =
        let reg = WorkspaceRegistry()
        for (key, path) in workspaces do
            let defs = WorkspaceLoader.loadWorkspace path
            reg.Register(WorkspaceId.create key, defs, sourcePath = path)
        reg
