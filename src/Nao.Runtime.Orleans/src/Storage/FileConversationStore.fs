namespace Nao.Runtime.Orleans

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

/// File-based conversation store.
/// Layout:
///   {baseDir}/
///     {sessionId}/
///       _meta.json              — session-level metadata
///       {conversationName}.jsonl — one JSON object per line (append-friendly)
///
/// Session IDs containing '/' are flattened to '_' for filesystem safety.
type FileConversationStore(baseDir: string) =

    let jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = false)
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        opts

    let sanitize (id: string) =
        id.Replace('/', '_').Replace('\\', '_').Replace(':', '_')

    let sessionDir (sessionId: string) =
        Path.Combine(baseDir, sanitize sessionId)

    let conversationFile (sessionId: string) (conversationName: string) =
        Path.Combine(sessionDir sessionId, sprintf "%s.jsonl" (sanitize conversationName))

    let metaFile (sessionId: string) (conversationName: string) =
        Path.Combine(sessionDir sessionId, sprintf "%s.meta.json" (sanitize conversationName))

    let ensureDir (sessionId: string) =
        let dir = sessionDir sessionId
        if not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore
        dir

    let serializeMessage (msg: PersistedMessage) =
        JsonSerializer.Serialize(msg, jsonOptions)

    let deserializeMessage (line: string) =
        JsonSerializer.Deserialize<PersistedMessage>(line, jsonOptions)

    let writeMeta (sessionId: string) (conversationName: string) (meta: ConversationMeta) =
        let path = metaFile sessionId conversationName
        let json = JsonSerializer.Serialize(meta, jsonOptions)
        File.WriteAllText(path, json)

    let readMeta (path: string) =
        try
            let json = File.ReadAllText(path)
            Some (JsonSerializer.Deserialize<ConversationMeta>(json, jsonOptions))
        with _ -> None

    interface IConversationStore with
        member _.AppendAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                if messages.Length = 0 then return ()
                else
                    ensureDir sessionId |> ignore
                    let path = conversationFile sessionId conversationName
                    let lines =
                        messages
                        |> Array.map serializeMessage
                    do! File.AppendAllLinesAsync(path, lines)

                    // Update metadata
                    let existing = metaFile sessionId conversationName |> readMeta
                    let now = DateTimeOffset.UtcNow
                    let lineCount =
                        if File.Exists path then File.ReadAllLines(path).Length else messages.Length
                    let meta =
                        match existing with
                        | Some m ->
                            { m with
                                LastMessageAt = now
                                MessageCount = lineCount }
                        | None ->
                            { SessionId = sessionId
                              ConversationName = conversationName
                              AgentName = ""
                              CreatedAt = now
                              LastMessageAt = now
                              MessageCount = lineCount }
                    writeMeta sessionId conversationName meta
            }

        member _.SaveAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                ensureDir sessionId |> ignore
                let path = conversationFile sessionId conversationName
                let lines = messages |> Array.map serializeMessage
                do! File.WriteAllLinesAsync(path, lines)

                let now = DateTimeOffset.UtcNow
                let meta =
                    { SessionId = sessionId
                      ConversationName = conversationName
                      AgentName = ""
                      CreatedAt = now
                      LastMessageAt = now
                      MessageCount = messages.Length }
                writeMeta sessionId conversationName meta
            }

        member _.LoadAsync (sessionId: string) (conversationName: string) =
            task {
                let path = conversationFile sessionId conversationName
                if File.Exists path then
                    let! lines = File.ReadAllLinesAsync(path)
                    return
                        lines
                        |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
                        |> Array.map deserializeMessage
                else
                    return Array.empty
            }

        member _.ListConversationsAsync(sessionId: string) =
            task {
                let dir = sessionDir sessionId
                if Directory.Exists dir then
                    return
                        Directory.GetFiles(dir, "*.meta.json")
                        |> Array.choose readMeta
                else
                    return Array.empty
            }

        member _.ListSessionsAsync() =
            task {
                if Directory.Exists baseDir then
                    return
                        Directory.GetDirectories(baseDir)
                        |> Array.map Path.GetFileName
                else
                    return Array.empty
            }

        member _.DeleteConversationAsync (sessionId: string) (conversationName: string) =
            task {
                let path = conversationFile sessionId conversationName
                if File.Exists path then File.Delete(path)
                let meta = metaFile sessionId conversationName
                if File.Exists meta then File.Delete(meta)
            }

        member _.DeleteSessionAsync(sessionId: string) =
            task {
                let dir = sessionDir sessionId
                if Directory.Exists dir then
                    Directory.Delete(dir, recursive = true)
            }
