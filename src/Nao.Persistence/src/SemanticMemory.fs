namespace Nao.Persistence

open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

/// ADO.NET-backed ISemanticMemory. Embeddings are stored as JSON; similarity is
/// computed in-process so the implementation stays provider-agnostic.
type AdoSemanticMemory(embeddingProvider: IEmbeddingProvider, factory: IDbConnectionFactory) =

    let ensureAsync () =
        Ado.executeNonQuery
            factory
            "CREATE TABLE IF NOT EXISTS nao_semantic (\
                agent TEXT NOT NULL, \
                sem_key TEXT NOT NULL, \
                sem_content TEXT NOT NULL, \
                sem_embedding TEXT NOT NULL, \
                sem_ts TEXT NOT NULL, \
                sem_tags TEXT NOT NULL, \
                PRIMARY KEY (agent, sem_key))"
            []
        :> Task

    let mapEntry (r: DbDataReader) : SemanticEntry =
        { Key = Ado.getString r "sem_key"
          Content = Ado.getString r "sem_content"
          Embedding = Json.floatsFromJson (Ado.getString r "sem_embedding")
          Timestamp = Time.fromIso (Ado.getString r "sem_ts")
          Tags = Json.tagsFromJson (Ado.getString r "sem_tags") }

    interface ISemanticMemory with
        member _.StoreAsync (agentId: AgentId) (key: string) (content: string) =
            task {
                do! ensureAsync ()
                let! embedding = embeddingProvider.EmbedAsync content
                let agent = agentId.Name
                do!
                    Ado.executeTransaction
                        factory
                        [ "DELETE FROM nao_semantic WHERE agent = @a AND sem_key = @k",
                          [ "@a", box agent; "@k", box key ]
                          "INSERT INTO nao_semantic (agent, sem_key, sem_content, sem_embedding, sem_ts, sem_tags) \
                                VALUES (@a, @k, @c, @e, @t, @g)",
                          [ "@a", box agent
                            "@k", box key
                            "@c", box content
                            "@e", box (Json.floatsToJson embedding)
                            "@t", box (Time.toIso System.DateTimeOffset.UtcNow)
                            "@g", box (Json.tagsToJson []) ] ]
            }

        member _.RetrieveAsync (agentId: AgentId) (query: string) (topK: int) =
            task {
                do! ensureAsync ()
                let! queryEmbedding = embeddingProvider.EmbedAsync query
                let! entries =
                    Ado.query
                        factory
                        "SELECT sem_key, sem_content, sem_embedding, sem_ts, sem_tags FROM nao_semantic WHERE agent = @a"
                        [ "@a", box agentId.Name ]
                        mapEntry
                return
                    entries
                    |> List.map (fun e -> e, SemanticSimilarity.cosineSimilarity queryEmbedding e.Embedding)
                    |> List.sortByDescending snd
                    |> List.truncate topK
                    |> List.map fst
            }

        member _.RemoveAsync (agentId: AgentId) (key: string) =
            task {
                do! ensureAsync ()
                let! _ =
                    Ado.executeNonQuery
                        factory
                        "DELETE FROM nao_semantic WHERE agent = @a AND sem_key = @k"
                        [ "@a", box agentId.Name; "@k", box key ]
                return ()
            }

/// FileSystem-backed ISemanticMemory. One JSON document per agent.
type FileSemanticMemory(embeddingProvider: IEmbeddingProvider, baseDir: string) =
    let sync = obj ()

    let agentFile (agentId: AgentId) =
        Path.Combine(baseDir, sprintf "%s.json" (Sanitize.id agentId.Name))

    let load (agentId: AgentId) : Dto.SemanticEntryDto list =
        FileJson.read<Dto.SemanticEntryDto list> (agentFile agentId) []

    let save (agentId: AgentId) (entries: Dto.SemanticEntryDto list) =
        FileJson.write (agentFile agentId) entries

    interface ISemanticMemory with
        member _.StoreAsync (agentId: AgentId) (key: string) (content: string) =
            task {
                let! embedding = embeddingProvider.EmbedAsync content
                let entry: SemanticEntry =
                    { Key = key
                      Content = content
                      Embedding = embedding
                      Timestamp = System.DateTimeOffset.UtcNow
                      Tags = [] }
                lock sync (fun () ->
                    let existing = load agentId |> List.filter (fun e -> e.Key <> key)
                    save agentId (Dto.toSemanticDto entry :: existing))
            }

        member _.RetrieveAsync (agentId: AgentId) (query: string) (topK: int) =
            task {
                let! queryEmbedding = embeddingProvider.EmbedAsync query
                let entries = lock sync (fun () -> load agentId |> List.map Dto.ofSemanticDto)
                return
                    entries
                    |> List.map (fun e -> e, SemanticSimilarity.cosineSimilarity queryEmbedding e.Embedding)
                    |> List.sortByDescending snd
                    |> List.truncate topK
                    |> List.map fst
            }

        member _.RemoveAsync (agentId: AgentId) (key: string) =
            task {
                lock sync (fun () ->
                    let remaining = load agentId |> List.filter (fun e -> e.Key <> key)
                    save agentId remaining)
            }

/// Factory helpers for semantic memory implementations.
module SemanticMemories =
    /// ADO.NET-backed semantic memory over any provider supplied via the connection factory.
    let ado (embeddingProvider: IEmbeddingProvider) (factory: IDbConnectionFactory) : ISemanticMemory =
        AdoSemanticMemory(embeddingProvider, factory) :> ISemanticMemory

    /// FileSystem-backed semantic memory rooted at the given directory.
    let file (embeddingProvider: IEmbeddingProvider) (baseDir: string) : ISemanticMemory =
        FileSemanticMemory(embeddingProvider, baseDir) :> ISemanticMemory
