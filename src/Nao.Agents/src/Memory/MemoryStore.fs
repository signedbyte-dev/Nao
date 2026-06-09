namespace Nao.Agents

open System
open System.Threading.Tasks

/// A single memory entry stored by an agent
type MemoryEntry =
    { Key: string
      Value: string
      Timestamp: DateTimeOffset
      Tags: string list }

/// Interface for persisting and retrieving agent memories
type IMemoryStore =
    /// Save a memory entry for an agent
    abstract member SaveAsync: AgentId -> MemoryEntry -> Task<unit>

    /// Recall memories by key prefix match
    abstract member RecallAsync: AgentId -> string -> Task<MemoryEntry list>

    /// Recall all memories for an agent
    abstract member RecallAllAsync: AgentId -> Task<MemoryEntry list>

    /// Forget (delete) a memory by key
    abstract member ForgetAsync: AgentId -> string -> Task<unit>

    /// Clear all memories for an agent
    abstract member ClearAsync: AgentId -> Task<unit>

/// In-memory implementation of IMemoryStore for testing and simple scenarios
type InMemoryStore() =
    let store = System.Collections.Concurrent.ConcurrentDictionary<string, MemoryEntry list>()

    let agentKey (agentId: AgentId) = agentId.Name

    interface IMemoryStore with
        member _.SaveAsync (agentId: AgentId) (entry: MemoryEntry) =
            let key = agentKey agentId
            store.AddOrUpdate(
                key,
                [ entry ],
                fun _ existing ->
                    let filtered = existing |> List.filter (fun e -> e.Key <> entry.Key)
                    entry :: filtered)
            |> ignore
            task { return () }

        member _.RecallAsync (agentId: AgentId) (queryKey: string) =
            let key = agentKey agentId
            match store.TryGetValue(key) with
            | true, entries ->
                entries
                |> List.filter (fun e -> e.Key.Contains(queryKey, StringComparison.OrdinalIgnoreCase))
                |> Task.FromResult
            | false, _ -> Task.FromResult([])

        member _.RecallAllAsync (agentId: AgentId) =
            let key = agentKey agentId
            match store.TryGetValue(key) with
            | true, entries -> Task.FromResult(entries)
            | false, _ -> Task.FromResult([])

        member _.ForgetAsync (agentId: AgentId) (entryKey: string) =
            let key = agentKey agentId
            match store.TryGetValue(key) with
            | true, entries ->
                let filtered = entries |> List.filter (fun e -> e.Key <> entryKey)
                store.[key] <- filtered
            | false, _ -> ()
            task { return () }

        member _.ClearAsync (agentId: AgentId) =
            let key = agentKey agentId
            store.TryRemove(key) |> ignore
            task { return () }
