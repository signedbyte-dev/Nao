namespace Nao.Agents

open System
open System.Threading.Tasks
open System.Collections.Concurrent

/// A scratchpad item in working memory with priority/attention weight
type WorkingMemoryItem =
    { Key: string
      Content: string
      /// Priority/attention weight — higher means more relevant to current task
      Attention: float
      /// Source of this item (e.g., "tool:search", "memory:long-term", "user:input")
      Source: string
      /// When added to working memory
      AddedAt: DateTimeOffset
      /// TTL in working memory before auto-decay
      ExpiresAt: DateTimeOffset option
      /// Whether this is pinned (immune to eviction)
      Pinned: bool }

/// Configuration for working memory
type WorkingMemoryConfig =
    { /// Maximum number of items in working memory
      Capacity: int
      /// Default TTL for unpinned items
      DefaultTtl: TimeSpan
      /// Attention decay rate per retrieval cycle (0.0 - 1.0)
      DecayRate: float
      /// Minimum attention threshold before eviction
      EvictionThreshold: float }

    static member Default =
        { Capacity = 15
          DefaultTtl = TimeSpan.FromMinutes 30.0
          DecayRate = 0.05
          EvictionThreshold = 0.1 }

/// Interface for task-scoped working memory (scratchpad)
type IWorkingMemory =
    /// Add or update an item in working memory
    abstract member SetAsync: WorkingMemoryItem -> Task<unit>
    /// Get an item by key (boosts its attention)
    abstract member GetAsync: key: string -> Task<WorkingMemoryItem option>
    /// Get all items sorted by attention (highest first)
    abstract member GetAllAsync: unit -> Task<WorkingMemoryItem list>
    /// Get items above an attention threshold
    abstract member GetActiveAsync: minAttention: float -> Task<WorkingMemoryItem list>
    /// Boost attention for a specific item
    abstract member FocusAsync: key: string -> boost: float -> Task<unit>
    /// Apply decay to all non-pinned items and evict expired/below-threshold
    abstract member DecayAsync: unit -> Task<int>
    /// Pin an item (prevent eviction)
    abstract member PinAsync: key: string -> Task<unit>
    /// Unpin an item
    abstract member UnpinAsync: key: string -> Task<unit>
    /// Remove a specific item
    abstract member RemoveAsync: key: string -> Task<unit>
    /// Clear all working memory
    abstract member ClearAsync: unit -> Task<unit>
    /// Render working memory as context for LLM (top-K by attention)
    abstract member RenderContextAsync: topK: int -> Task<string>

/// In-memory implementation of working memory
type InMemoryWorkingMemory(config: WorkingMemoryConfig) =
    let items = ConcurrentDictionary<string, WorkingMemoryItem>()

    let evictOverCapacity () =
        if items.Count > config.Capacity then
            let toEvict =
                items.Values
                |> Seq.filter (fun i -> not i.Pinned)
                |> Seq.sortBy (fun i -> i.Attention)
                |> Seq.truncate (items.Count - config.Capacity)
                |> Seq.toList
            for item in toEvict do
                items.TryRemove(item.Key) |> ignore

    interface IWorkingMemory with
        member _.SetAsync(item: WorkingMemoryItem) =
            let withExpiry =
                match item.ExpiresAt with
                | Some _ -> item
                | None -> { item with ExpiresAt = Some (DateTimeOffset.UtcNow + config.DefaultTtl) }
            items.AddOrUpdate(item.Key, withExpiry, fun _ _ -> withExpiry) |> ignore
            evictOverCapacity ()
            task { return () }

        member _.GetAsync(key: string) =
            match items.TryGetValue(key) with
            | true, item ->
                // Boost attention on access
                let boosted = { item with Attention = min 1.0 (item.Attention + 0.1) }
                items.TryUpdate(key, boosted, item) |> ignore
                Task.FromResult(Some boosted)
            | false, _ -> Task.FromResult(None)

        member _.GetAllAsync() =
            items.Values
            |> Seq.sortByDescending (fun i -> i.Attention)
            |> Seq.toList
            |> Task.FromResult

        member _.GetActiveAsync(minAttention: float) =
            items.Values
            |> Seq.filter (fun i -> i.Attention >= minAttention)
            |> Seq.sortByDescending (fun i -> i.Attention)
            |> Seq.toList
            |> Task.FromResult

        member _.FocusAsync (key: string) (boost: float) =
            match items.TryGetValue(key) with
            | true, item ->
                let focused = { item with Attention = min 1.0 (item.Attention + boost) }
                items.TryUpdate(key, focused, item) |> ignore
            | false, _ -> ()
            task { return () }

        member _.DecayAsync() =
            task {
                let now = DateTimeOffset.UtcNow
                let mutable evicted = 0
                for kvp in items do
                    let item = kvp.Value
                    if item.Pinned then () // Skip pinned
                    else
                        // Check expiry
                        match item.ExpiresAt with
                        | Some exp when now > exp ->
                            items.TryRemove(kvp.Key) |> ignore
                            evicted <- evicted + 1
                        | _ ->
                            // Apply decay
                            let decayed = { item with Attention = item.Attention * (1.0 - config.DecayRate) }
                            if decayed.Attention < config.EvictionThreshold then
                                items.TryRemove(kvp.Key) |> ignore
                                evicted <- evicted + 1
                            else
                                items.TryUpdate(kvp.Key, decayed, item) |> ignore
                return evicted
            }

        member _.PinAsync(key: string) =
            match items.TryGetValue(key) with
            | true, item ->
                items.TryUpdate(key, { item with Pinned = true; ExpiresAt = None }, item) |> ignore
            | false, _ -> ()
            task { return () }

        member _.UnpinAsync(key: string) =
            match items.TryGetValue(key) with
            | true, item ->
                let unpinned =
                    { item with
                        Pinned = false
                        ExpiresAt = Some (DateTimeOffset.UtcNow + config.DefaultTtl) }
                items.TryUpdate(key, unpinned, item) |> ignore
            | false, _ -> ()
            task { return () }

        member _.RemoveAsync(key: string) =
            items.TryRemove(key) |> ignore
            task { return () }

        member _.ClearAsync() =
            items.Clear()
            task { return () }

        member _.RenderContextAsync(topK: int) =
            let active =
                items.Values
                |> Seq.sortByDescending (fun i -> i.Attention)
                |> Seq.truncate topK
                |> Seq.toList
            let rendered =
                active
                |> List.mapi (fun idx item ->
                    sprintf "[%d] (%s, attention=%.2f) %s" (idx + 1) item.Source item.Attention item.Content)
                |> String.concat "\n"
            Task.FromResult(rendered)
