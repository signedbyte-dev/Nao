namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Core

/// A memory entry with an embedding vector for semantic retrieval
type SemanticEntry =
    { Key: string
      Content: string
      Embedding: float array
      Timestamp: DateTimeOffset
      Tags: string list }

/// Interface for generating text embeddings
type IEmbeddingProvider =
    /// Generate an embedding vector for the given text
    abstract member EmbedAsync: string -> Task<float array>

/// Semantic memory store that uses embeddings for similarity-based retrieval
type ISemanticMemory =
    /// Store a memory with its embedding
    abstract member StoreAsync: AgentId -> string -> string -> Task<unit>

    /// Retrieve the top-k most relevant memories for a query
    abstract member RetrieveAsync: AgentId -> string -> int -> Task<SemanticEntry list>

    /// Remove a memory by key
    abstract member RemoveAsync: AgentId -> string -> Task<unit>

module SemanticSimilarity =

    /// Compute cosine similarity between two vectors (handles different lengths by zero-padding)
    let cosineSimilarity (a: float array) (b: float array) =
        if a.Length = 0 && b.Length = 0 then 0.0
        else
            let maxLen = max a.Length b.Length
            let mutable dot = 0.0
            let mutable normA = 0.0
            let mutable normB = 0.0
            for i in 0 .. maxLen - 1 do
                let ai = if i < a.Length then a.[i] else 0.0
                let bi = if i < b.Length then b.[i] else 0.0
                dot <- dot + ai * bi
                normA <- normA + ai * ai
                normB <- normB + bi * bi
            if normA = 0.0 || normB = 0.0 then 0.0
            else dot / (sqrt normA * sqrt normB)

/// In-memory semantic memory implementation
type InMemorySemanticMemory(embeddingProvider: IEmbeddingProvider) =
    let store = System.Collections.Concurrent.ConcurrentDictionary<string, SemanticEntry list>()

    let agentKey (agentId: AgentId) = agentId.Name

    interface ISemanticMemory with
        member _.StoreAsync (agentId: AgentId) (key: string) (content: string) =
            task {
                let! embedding = embeddingProvider.EmbedAsync content
                let entry =
                    { Key = key
                      Content = content
                      Embedding = embedding
                      Timestamp = DateTimeOffset.UtcNow
                      Tags = [] }
                let storeKey = agentKey agentId
                store.AddOrUpdate(
                    storeKey,
                    [ entry ],
                    fun _ existing ->
                        let filtered = existing |> List.filter (fun e -> e.Key <> key)
                        entry :: filtered)
                |> ignore
            }

        member _.RetrieveAsync (agentId: AgentId) (query: string) (topK: int) =
            task {
                let! queryEmbedding = embeddingProvider.EmbedAsync query
                let storeKey = agentKey agentId
                match store.TryGetValue(storeKey) with
                | true, entries ->
                    return
                        entries
                        |> List.map (fun e -> (e, SemanticSimilarity.cosineSimilarity queryEmbedding e.Embedding))
                        |> List.sortByDescending snd
                        |> List.truncate topK
                        |> List.map fst
                | false, _ -> return []
            }

        member _.RemoveAsync (agentId: AgentId) (key: string) =
            let storeKey = agentKey agentId
            match store.TryGetValue(storeKey) with
            | true, entries ->
                store.[storeKey] <- entries |> List.filter (fun e -> e.Key <> key)
            | false, _ -> ()
            task { return () }

/// A simple bag-of-words embedding provider for testing (no external dependencies)
type SimpleEmbeddingProvider() =
    let vocabulary = System.Collections.Concurrent.ConcurrentDictionary<string, int>()
    let mutable nextIndex = 0

    let getIndex (word: string) =
        vocabulary.GetOrAdd(word, fun _ ->
            let idx = nextIndex
            nextIndex <- nextIndex + 1
            idx)

    interface IEmbeddingProvider with
        member _.EmbedAsync (text: string) =
            let words =
                text.ToLowerInvariant().Split([| ' '; '.'; ','; '!'; '?'; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            // Build a sparse vector using word frequencies
            let wordCounts = System.Collections.Generic.Dictionary<int, float>()
            for word in words do
                let idx = getIndex word
                match wordCounts.TryGetValue(idx) with
                | true, count -> wordCounts.[idx] <- count + 1.0
                | false, _ -> wordCounts.[idx] <- 1.0

            // Create a dense vector up to current vocabulary size
            let size = max nextIndex 1
            let vector = Array.zeroCreate<float> size
            for kvp in wordCounts do
                if kvp.Key < size then
                    vector.[kvp.Key] <- kvp.Value
            Task.FromResult(vector)
