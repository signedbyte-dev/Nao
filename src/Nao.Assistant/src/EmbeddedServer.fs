namespace Nao.Assistant

open System
open System.IO
open System.Net.WebSockets
open System.Net.Sockets
open System.Data.Common
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Persistence
open Nao.Feedback
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

/// Check if an LLM provider endpoint is reachable
module ProviderHealth =

    let checkAsync (settings: ProviderSettings) : Task<Result<string, string>> =
        task {
            try
                use client = new System.Net.Http.HttpClient()
                client.Timeout <- TimeSpan.FromSeconds(5.0)
                match settings.ProviderType.ToLowerInvariant() with
                | "ollama" ->
                    let url = if String.IsNullOrWhiteSpace(settings.Endpoint) then "http://localhost:11434" else settings.Endpoint
                    let! resp = client.GetAsync(url + "/api/tags")
                    if resp.IsSuccessStatusCode then
                        return Ok (sprintf "Ollama is running at %s" url)
                    else
                        return Error (sprintf "Ollama returned %d at %s" (int resp.StatusCode) url)
                | "openai" ->
                    let url = if String.IsNullOrWhiteSpace(settings.Endpoint) then "https://api.openai.com/v1" else settings.Endpoint
                    let! resp = client.GetAsync(url + "/models")
                    if int resp.StatusCode < 500 then
                        return Ok (sprintf "OpenAI endpoint reachable at %s" url)
                    else
                        return Error (sprintf "OpenAI endpoint returned %d" (int resp.StatusCode))
                | "anthropic" ->
                    return Ok "Anthropic (API key validated at runtime)"
                | other ->
                    return Ok (sprintf "Provider '%s' — no health check available" other)
            with ex ->
                return Error (sprintf "Cannot reach provider: %s" ex.Message)
        }

module AssistantTools =

    let private workDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nao-demo-workspace")

    let private resolvePath (input: string) =
        let name = input.Trim().Replace("\\", "/").TrimStart('/')
        Path.GetFullPath(Path.Combine(workDir, name))

    let ensureWorkDir () =
        Directory.CreateDirectory(workDir) |> ignore
        workDir

    let createFolder: Tool =
        Tool.Create("create_folder", "Create a new folder. Input: relative folder path.",
            fun input -> task {
                let path = resolvePath input
                Directory.CreateDirectory(path) |> ignore
                return sprintf """{"created":"%s","exists":true}""" (path.Replace("\\", "/"))
            })

    let writeFile: Tool =
        Tool.Create("write_file", "Write content to a file. Input format: 'relative/path|content'.",
            fun input -> task {
                let parts = input.Split('|', 2)
                if parts.Length < 2 then return """{"error":"Expected 'path|content'"}"""
                else
                    let path = resolvePath parts.[0]
                    let dir = Path.GetDirectoryName(path)
                    if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
                    do! File.WriteAllTextAsync(path, parts.[1])
                    return sprintf """{"written":"%s","bytes":%d}""" (path.Replace("\\", "/")) parts.[1].Length
            })

    let readFile: Tool =
        Tool.Create("read_file", "Read content from a file. Input: relative file path.",
            fun input -> task {
                let path = resolvePath input
                if File.Exists(path) then
                    let! content = File.ReadAllTextAsync(path)
                    return content
                else
                    return sprintf """{"error":"File not found: %s"}""" input
            })

    let listFolder: Tool =
        Tool.Create("list_folder", "List directory contents. Input: relative path (empty for root).",
            fun input -> task {
                let path = if String.IsNullOrWhiteSpace(input) then workDir else resolvePath input
                if not (Directory.Exists(path)) then
                    return sprintf """{"error":"Directory not found: %s"}""" input
                else
                    let entries =
                        Directory.GetFileSystemEntries(path)
                        |> Array.map (fun e ->
                            let name = Path.GetFileName(e)
                            let isDir = Directory.Exists(e)
                            sprintf """{"name":"%s","type":"%s"}""" name (if isDir then "dir" else "file"))
                        |> String.concat ","
                    return sprintf """{"path":"%s","entries":[%s]}""" (path.Replace("\\", "/")) entries
            })

    let delete: Tool =
        Tool.Create("delete", "Delete a file or folder. Input: relative path.",
            fun input -> task {
                let path = resolvePath input
                if File.Exists(path) then
                    File.Delete(path)
                    return sprintf """{"deleted":"%s","type":"file"}""" input
                elif Directory.Exists(path) then
                    Directory.Delete(path, true)
                    return sprintf """{"deleted":"%s","type":"dir"}""" input
                else
                    return sprintf """{"error":"Not found: %s"}""" input
            })

    let dateTime: Tool =
        Tool.Create("get_datetime", "Get the current date and time.",
            fun _ -> task {
                return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
            })

    let calculator: Tool =
        Tool.Create("calculator", "Evaluate a simple math expression. Input: expression like '2 + 3'.",
            fun input -> task {
                try
                    let parts = input.Trim().Split(' ')
                    if parts.Length = 3 then
                        let a = Double.Parse(parts.[0])
                        let b = Double.Parse(parts.[2])
                        let result =
                            match parts.[1] with
                            | "+" -> a + b | "-" -> a - b
                            | "*" -> a * b | "/" -> if b <> 0.0 then a / b else Double.NaN
                            | _ -> Double.NaN
                        return sprintf """{"result":%g}""" result
                    else
                        return """{"error":"Expected format: 'a op b'"}"""
                with ex ->
                    return sprintf """{"error":"%s"}""" ex.Message
            })

    let allTools = [ createFolder; writeFile; readFile; listFolder; delete; dateTime; calculator ]


module Database =

    let dataDir =
        // Default to a folder under the current working directory so the app's data
        // (SQLite db, conversations, observability) lands in the repo and can be
        // inspected. Override with NAO_DATA_DIR to point elsewhere.
        let dir =
            match Environment.GetEnvironmentVariable("NAO_DATA_DIR") with
            | path when not (String.IsNullOrWhiteSpace path) -> path
            | _ -> Path.Combine(Environment.CurrentDirectory, ".nao-data")
        Directory.CreateDirectory(dir) |> ignore
        dir

    let dbPath = Path.Combine(dataDir, "nao.db")
    let connectionString = sprintf "Data Source=%s;" dbPath

    let initialize () =
        DbProviderFactories.RegisterFactory("System.Data.SQLite", Microsoft.Data.Sqlite.SqliteFactory.Instance)

        use conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            CREATE TABLE IF NOT EXISTS OrleansQuery (
                QueryKey TEXT NOT NULL,
                QueryText TEXT NOT NULL,
                CONSTRAINT OrleansQuery_Key PRIMARY KEY (QueryKey)
            );

            CREATE TABLE IF NOT EXISTS OrleansStorage (
                GrainIdHash INT NOT NULL,
                GrainIdN0 BIGINT NOT NULL,
                GrainIdN1 BIGINT NOT NULL,
                GrainTypeHash INT NOT NULL,
                GrainTypeString NVARCHAR(512) NOT NULL,
                GrainIdExtensionString NVARCHAR(512) NULL,
                ServiceId NVARCHAR(150) NOT NULL,
                PayloadBinary BLOB NULL,
                ModifiedOn DATETIME NOT NULL,
                Version INT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_OrleansStorage ON OrleansStorage(GrainIdHash, GrainTypeHash);

            INSERT OR IGNORE INTO OrleansQuery (QueryKey, QueryText) VALUES
            ('WriteToStorageKey', '
                BEGIN TRANSACTION;

                CREATE TEMP TABLE IF NOT EXISTS OrleansStorageWriteState
                (
                    TotalChangesBefore INT NOT NULL
                );
                DELETE FROM OrleansStorageWriteState;
                INSERT INTO OrleansStorageWriteState (TotalChangesBefore) VALUES (total_changes() + 1);

                UPDATE OrleansStorage
                SET
                    PayloadBinary = @PayloadBinary,
                    ModifiedOn = datetime(''now''),
                    Version = Version + 1
                WHERE
                    GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId
                    AND Version = @GrainStateVersion;

                INSERT INTO OrleansStorage (GrainIdHash, GrainIdN0, GrainIdN1, GrainTypeHash, GrainTypeString, GrainIdExtensionString, ServiceId, PayloadBinary, ModifiedOn, Version)
                SELECT @GrainIdHash, @GrainIdN0, @GrainIdN1, @GrainTypeHash, @GrainTypeString, @GrainIdExtensionString, @ServiceId, @PayloadBinary, datetime(''now''), 1
                WHERE changes() = 0
                  AND @GrainStateVersion IS NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM OrleansStorage
                    WHERE GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                  );

                SELECT Version AS NewGrainStateVersion FROM OrleansStorage
                WHERE total_changes() > (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
                    AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId;

                SELECT @GrainStateVersion AS NewGrainStateVersion
                WHERE total_changes() = (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
                    AND @GrainStateVersion IS NOT NULL;

                COMMIT;
            ');

            INSERT OR IGNORE INTO OrleansQuery (QueryKey, QueryText) VALUES
            ('ReadFromStorageKey', '
                SELECT
                    PayloadBinary,
                    Version AS Version
                FROM
                    OrleansStorage
                WHERE
                    GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId
                LIMIT 1;
            ');

            INSERT OR IGNORE INTO OrleansQuery (QueryKey, QueryText) VALUES
            ('ClearStorageKey', '
                UPDATE OrleansStorage
                SET
                    PayloadBinary = NULL,
                    ModifiedOn = datetime(''now''),
                    Version = Version + 1
                WHERE
                    GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId
                    AND Version = @GrainStateVersion;

                SELECT Version AS NewGrainStateVersion FROM OrleansStorage
                WHERE changes() > 0
                    AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId;

                SELECT @GrainStateVersion AS NewGrainStateVersion
                WHERE changes() = 0
                    AND @GrainStateVersion IS NOT NULL;
            ');
        """
        cmd.ExecuteNonQuery() |> ignore


module EmbeddedServer =

    let private waitForListener (port: int) (timeout: TimeSpan) =
        let deadline = DateTime.UtcNow + timeout
        let mutable started = false
        let mutable lastError = "no connection attempt made"

        while not started && DateTime.UtcNow < deadline do
            try
                use client = new TcpClient()
                let connected = client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(300.0))
                if connected && client.Connected then
                    started <- true
                else
                    lastError <- "connection attempt timed out"
            with ex ->
                lastError <- ex.Message

            if not started then
                Thread.Sleep(200)

        if not started then
            failwithf "Embedded server failed to start on localhost:%d within %0.1f seconds (%s)" port timeout.TotalSeconds lastError

    let private jsonOptions =
        let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        opts

    let private sendWs (socket: WebSocket) (resp: WsResponse) = task {
        let json = JsonSerializer.Serialize(resp, jsonOptions)
        let bytes = Encoding.UTF8.GetBytes(json)
        do! socket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
    }

    let private handleWsMessage (socket: WebSocket) (grainFactory: IGrainFactory) (sessionId: string) (msg: WsRequest) = task {
        let session = grainFactory.GetGrain<ISessionGrain>(sessionId)
        try
            match msg.Type with
            | WsRequestType.Chat ->
                let! response = session.ProcessAsync(msg.Payload)
                do! sendWs socket { Type = WsResponseType.Done; Payload = response }

            | WsRequestType.Info ->
                let! info = session.GetInfoAsync()
                let payload = JsonSerializer.Serialize(
                    {| sessionId = info.SessionId; agentName = info.AgentName
                       workspaceKey = info.WorkspaceKey; activeConversation = info.ActiveConversation
                       isActive = info.IsActive; createdAt = info.CreatedAt; lastActiveAt = info.LastActiveAt |}, jsonOptions)
                do! sendWs socket { Type = WsResponseType.Info; Payload = payload }

            | WsRequestType.History ->
                let! history = session.GetHistoryAsync()
                let dtos =
                    history |> Array.map (fun m ->
                        { MessageDto.Role = m.Role.ToLowerInvariant()
                          Content = m.Content })
                let payload = JsonSerializer.Serialize(dtos, jsonOptions)
                do! sendWs socket { Type = WsResponseType.History; Payload = payload }

            | WsRequestType.Clear ->
                do! session.ClearHistoryAsync()
                do! sendWs socket { Type = WsResponseType.Done; Payload = "History cleared" }

            | WsRequestType.Conversations ->
                let! convs = session.ListConversationsAsync()
                let payload = JsonSerializer.Serialize(convs, jsonOptions)
                do! sendWs socket { Type = WsResponseType.Conversations; Payload = payload }

            | WsRequestType.Switch ->
                do! session.SwitchConversationAsync(msg.Payload)
                do! sendWs socket { Type = WsResponseType.Done; Payload = sprintf "Switched to: %s" msg.Payload }

            | _ ->
                do! sendWs socket { Type = WsResponseType.Error; Payload = "Unknown request type" }
        with ex ->
            do! sendWs socket { Type = WsResponseType.Error; Payload = ex.Message }
    }

    let private handleWebSocket (ctx: HttpContext) (grainFactory: IGrainFactory) (sessionId: string) = task {
        let! socket = ctx.WebSockets.AcceptWebSocketAsync()
        let buffer = Array.zeroCreate<byte> 8192

        try
            let mutable running = true
            while running && socket.State = WebSocketState.Open do
                let segments = ResizeArray<byte>()
                let mutable endOfMessage = false
                while not endOfMessage do
                    let! result = socket.ReceiveAsync(ArraySegment(buffer), CancellationToken.None)
                    if result.MessageType = WebSocketMessageType.Close then
                        do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                        running <- false
                        endOfMessage <- true
                    else
                        segments.AddRange(buffer.[0..result.Count - 1])
                        endOfMessage <- result.EndOfMessage

                if running && segments.Count > 0 then
                    let json = Encoding.UTF8.GetString(segments.ToArray())
                    try
                        let msg = JsonSerializer.Deserialize<WsRequest>(json, jsonOptions)
                        do! handleWsMessage socket grainFactory sessionId msg
                    with ex ->
                        do! sendWs socket { Type = WsResponseType.Error; Payload = sprintf "Invalid message: %s" ex.Message }
        with
        | :? WebSocketException -> ()
    }

    let mutable private host: WebApplication option = None
    let mutable private cts: CancellationTokenSource option = None

    /// Mutable handler for tool confirmation requests.
    /// The UI layer sets this to show a popup dialog.
    let mutable confirmationHandler: ToolConfirmationRequest -> unit =
        fun req -> req.Completion.TrySetResult(true) |> ignore

    /// Set the handler that will be called when the orchestrator needs tool confirmation.
    let setConfirmationHandler (handler: ToolConfirmationRequest -> unit) =
        confirmationHandler <- handler

    // ─────────────────────────────────────────────────────────────────────────
    // Feedback / suggestion enhancement loop — shared, Orleans-independent core.
    //
    // These helpers and endpoint mappings depend only on FeedbackService and
    // IWorkspaceRegistry, so they can be hosted standalone (see startEnhancementHost)
    // for fast integration tests without booting the Orleans silo or an LLM.
    // ─────────────────────────────────────────────────────────────────────────

    /// Load the on-disk workspace and merge the built-in assistant tools.
    let loadMergedWorkspace (workspaceRoot: string) =
        let workspace = WorkspaceLoader.loadWorkspace workspaceRoot
        { workspace with Tools = workspace.Tools @ AssistantTools.allTools }

    /// Reload the workspace from disk and overwrite the default registry entry.
    /// Used after a user registers (or promotes) a new tool/agent definition so
    /// it becomes resolvable without restarting the app.
    let reloadWorkspaceAt (workspaceRoot: string) (registry: IWorkspaceRegistry) =
        registry.Register(WorkspaceId.defaultId, loadMergedWorkspace workspaceRoot)

    /// Build a CANDIDATE workspace from all Confirmed suggestions: the proposed
    /// improvement overlays are baked into a fresh copy of the workspace and
    /// registered under the "candidate" key, so the user can start a test session
    /// against the enhanced tools/agents without touching the live "default" one.
    let buildCandidateAt (workspaceRoot: string) (feedback: FeedbackService) (registry: IWorkspaceRegistry) =
        task {
            let! confirmed = feedback.GetSuggestionsByStatusAsync SuggestionStatus.Confirmed
            let anns = confirmed |> List.choose (fun s -> s.ProposedAnnotation)
            let toolAnns = anns |> List.filter (fun a -> a.Kind = AnnotationKind.Tool)
            let agentAnns = anns |> List.filter (fun a -> a.Kind = AnnotationKind.Agent)
            let workspace = WorkspaceLoader.loadWorkspace workspaceRoot
            let mergedTools = workspace.Tools @ AssistantTools.allTools
            let candidate =
                { workspace with
                    Tools = Annotations.applyToolAnnotations toolAnns mergedTools
                    AgentDefs = workspace.AgentDefs |> List.map (Annotations.applyAgentAnnotations agentAnns) }
            registry.Register(WorkspaceId.create "candidate", candidate)
            return confirmed
        }

    /// Upgrade the live system from all Confirmed suggestions: bake each improvement
    /// into the canonical workspace definition (rewriting the JSON file when the
    /// target is JSON-sourced, otherwise persisting a durable live annotation as a
    /// fallback), mark the suggestion Applied, reload "default", and drop the
    /// candidate. This is the irreversible "make it permanent" step.
    let upgradeCandidateAt (workspaceRoot: string) (feedback: FeedbackService) (registry: IWorkspaceRegistry) =
        task {
            let! confirmed = feedback.GetSuggestionsByStatusAsync SuggestionStatus.Confirmed
            let live = registry.TryGet WorkspaceId.defaultId
            let results = ResizeArray<{| target: string; kind: string; persisted: string; detail: string |}>()
            for s in confirmed do
                match s.ProposedAnnotation with
                | None -> ()
                | Some ann ->
                    let prov =
                        match ann.Provenance with
                        | Some p -> Some p
                        | None ->
                            match s.Kind, live with
                            | AnnotationKind.Agent, Some w ->
                                w.AgentDefs |> List.tryFind (fun d -> d.Name = s.TargetName) |> Option.bind (fun d -> d.Provenance)
                            | AnnotationKind.Tool, Some w ->
                                w.ToolDefs |> List.tryFind (fun d -> d.Name = s.TargetName) |> Option.bind (fun d -> d.Provenance)
                            | _ -> None
                    let rewrite =
                        match s.Kind with
                        | AnnotationKind.Tool -> Annotations.rewriteToolDefinition prov [ ann ]
                        | AnnotationKind.Agent -> Annotations.rewriteAgentDefinition prov [ ann ]
                    let kindLabel = match s.Kind with AnnotationKind.Tool -> "tool" | AnnotationKind.Agent -> "agent"
                    match rewrite with
                    | Ok path ->
                        let! _ = feedback.MarkSuggestionAppliedAsync s.Id
                        results.Add({| target = s.TargetName; kind = kindLabel; persisted = "file"; detail = path |})
                    | Error e ->
                        // Fallback: persist as a live annotation overlay so the
                        // improvement still sticks even without a JSON source file.
                        let! _ = feedback.AddAnnotationAsync ann
                        let! _ = feedback.MarkSuggestionAppliedAsync s.Id
                        results.Add({| target = s.TargetName; kind = kindLabel; persisted = "annotation"; detail = e |})
            reloadWorkspaceAt workspaceRoot registry
            registry.Remove(WorkspaceId.create "candidate") |> ignore
            return List.ofSeq results
        }

    /// Persist a user-supplied tool/agent JSON definition into the workspace and
    /// reload so it becomes resolvable. Returns the written file path.
    let registerDefinitionAt (workspaceRoot: string) (subdir: string) (req: RegisterDefinitionRequest) (registry: IWorkspaceRegistry) =
        let name = (req.Name |> Option.ofObj |> Option.defaultValue "").Trim()
        if String.IsNullOrEmpty name then Error "name is required"
        else
            let safe = name |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' then c else '_') |> Seq.toArray |> System.String
            let dir = Path.Combine(workspaceRoot, ".nao", subdir)
            Directory.CreateDirectory dir |> ignore
            let path = Path.Combine(dir, sprintf "%s.json" safe)
            let json = req.Definition.GetRawText()
            File.WriteAllText(path, json)
            reloadWorkspaceAt workspaceRoot registry
            Ok path

    let private parseKind (s: string) : AnnotationKind option =
        match (s |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
        | "tool" -> Some AnnotationKind.Tool
        | "agent" -> Some AnnotationKind.Agent
        | _ -> None

    let private strOpt (s: string) = if String.IsNullOrWhiteSpace s then None else Some s
    let private jsonResult (value: 'a) = Results.Content(FeedbackJson.serializeIndented value, "application/json")

    /// Register the services the enhancement endpoints depend on. Used by the
    /// standalone test host; the production `start` registers richer variants.
    let registerEnhancementServices (services: IServiceCollection) (workspaceRoot: string) (feedbackDir: string) =
        services.AddSingleton<IWorkspaceRegistry>(fun _ ->
            let registry = WorkspaceRegistry()
            registry.Register(WorkspaceId.defaultId, loadMergedWorkspace workspaceRoot)
            registry :> IWorkspaceRegistry) |> ignore
        services.AddSingleton<FeedbackService>(fun _ -> FeedbackService.File feedbackDir) |> ignore

    /// Map all feedback / annotation / version / suggestion / candidate / register
    /// endpoints onto the given app. Shared by the production server and the test host.
    let mapEnhancementEndpoints (app: WebApplication) (workspaceRoot: string) =
        let reloadWorkspace (registry: IWorkspaceRegistry) = reloadWorkspaceAt workspaceRoot registry
        let buildCandidate (feedback: FeedbackService) (registry: IWorkspaceRegistry) = buildCandidateAt workspaceRoot feedback registry
        let upgradeCandidate (feedback: FeedbackService) (registry: IWorkspaceRegistry) = upgradeCandidateAt workspaceRoot feedback registry
        let registerDefinition (subdir: string) (req: RegisterDefinitionRequest) (registry: IWorkspaceRegistry) = registerDefinitionAt workspaceRoot subdir req registry

        // ─── Annotations: persistent runtime overlays on tools/agents ───

        app.MapGet("/api/annotations", Func<FeedbackService, _>(fun feedback -> task {
            let! annotations = feedback.ListAnnotationsAsync()
            return jsonResult annotations
        })) |> ignore

        app.MapPost("/api/annotations", Func<HttpContext, FeedbackService, _>(fun ctx feedback -> task {
            let! req = ctx.Request.ReadFromJsonAsync<AnnotationRequest>()
            match parseKind req.Kind with
            | None -> return Results.BadRequest({| error = "kind must be 'tool' or 'agent'" |})
            | Some kind ->
                let baseAnn =
                    match kind with
                    | AnnotationKind.Tool -> Annotation.ForTool req.TargetName
                    | AnnotationKind.Agent -> Annotation.ForAgent req.TargetName
                let annotation =
                    { baseAnn with
                        BaseVersion = strOpt req.BaseVersion
                        DescriptionOverride = strOpt req.DescriptionOverride
                        DescriptionAppend = strOpt req.DescriptionAppend
                        InputPrefix = strOpt req.InputPrefix
                        OutputSuffix = strOpt req.OutputSuffix
                        GuidanceAppend = strOpt req.GuidanceAppend
                        Reason = strOpt req.Reason }
                let! stored = feedback.AddAnnotationAsync annotation
                return jsonResult stored
        })) |> ignore

        app.MapPost("/api/annotations/{id}/status", Func<HttpContext, FeedbackService, string, _>(fun ctx feedback id -> task {
            let! req = ctx.Request.ReadFromJsonAsync<AnnotationStatusRequest>()
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid annotation id" |})
            | true, guid ->
                let status =
                    match (req.Status |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
                    | "disabled" | "off" -> AnnotationStatus.Disabled
                    | _ -> AnnotationStatus.Active
                let! ok = feedback.SetAnnotationStatusAsync(guid, status)
                return (if ok then Results.Ok({| updated = true |}) else Results.NotFound())
        })) |> ignore

        app.MapDelete("/api/annotations/{id}", Func<FeedbackService, string, _>(fun feedback id -> task {
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid annotation id" |})
            | true, guid ->
                let! ok = feedback.DropAnnotationAsync guid
                return (if ok then Results.Ok({| dropped = true |}) else Results.NotFound())
        })) |> ignore

        // ─── Versions: reviewed Draft → Active → Deprecated lifecycle ───

        app.MapGet("/api/versions", Func<FeedbackService, _>(fun feedback -> task {
            let! versions = feedback.ListVersionsAsync()
            return jsonResult versions
        })) |> ignore

        app.MapPost("/api/versions/promote", Func<HttpContext, FeedbackService, IWorkspaceRegistry, _>(fun ctx feedback registry -> task {
            let! req = ctx.Request.ReadFromJsonAsync<PromoteVersionRequest>()
            match parseKind req.Kind with
            | None -> return Results.BadRequest({| error = "kind must be 'tool' or 'agent'" |})
            | Some kind ->
                let! version =
                    match strOpt req.Version with
                    | Some v -> feedback.PromoteAsync(kind, req.TargetName, version = v)
                    | None -> feedback.PromoteAsync(kind, req.TargetName)
                // Materialised definition (if any) becomes resolvable after reload.
                if version.Location.IsSome then reloadWorkspace registry
                return jsonResult version
        })) |> ignore

        app.MapPost("/api/versions/{id}/confirm", Func<HttpContext, FeedbackService, IWorkspaceRegistry, string, _>(fun ctx feedback registry id -> task {
            let! req = ctx.Request.ReadFromJsonAsync<ConfirmVersionRequest>()
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid version id" |})
            | true, guid ->
                let! ok = feedback.ConfirmVersionAsync(guid, req.ReplaceLegacy)
                if ok then reloadWorkspace registry
                return (if ok then Results.Ok({| confirmed = true |}) else Results.NotFound())
        })) |> ignore

        app.MapPost("/api/versions/{id}/deprecate", Func<FeedbackService, string, _>(fun feedback id -> task {
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid version id" |})
            | true, guid ->
                let! ok = feedback.DeprecateVersionAsync guid
                return (if ok then Results.Ok({| deprecated = true |}) else Results.NotFound())
        })) |> ignore

        // ─── Register user-supplied tools / agents ───

        app.MapPost("/api/register/tool", Func<HttpContext, IWorkspaceRegistry, _>(fun ctx registry -> task {
            let! req = ctx.Request.ReadFromJsonAsync<RegisterDefinitionRequest>()
            match registerDefinition "tools" req registry with
            | Ok path -> return Results.Ok({| registered = true; path = path |})
            | Error e -> return Results.BadRequest({| error = e |})
        })) |> ignore

        app.MapPost("/api/register/agent", Func<HttpContext, IWorkspaceRegistry, _>(fun ctx registry -> task {
            let! req = ctx.Request.ReadFromJsonAsync<RegisterDefinitionRequest>()
            match registerDefinition "agents" req registry with
            | Ok path -> return Results.Ok({| registered = true; path = path |})
            | Error e -> return Results.BadRequest({| error = e |})
        })) |> ignore

        // ─── Cross-session suggestions (review-gated enhancement pipeline) ───

        app.MapGet("/api/suggestions", Func<FeedbackService, _>(fun feedback -> task {
            let! suggestions = feedback.ListSuggestionsAsync()
            return jsonResult suggestions
        })) |> ignore

        // "Enhance the system": aggregate ALL feedback (explicit + implicit) across
        // every session into review-gated improvement suggestions.
        app.MapPost("/api/suggestions/generate", Func<FeedbackService, _>(fun feedback -> task {
            let! generated = feedback.GenerateSuggestionsAsync()
            return jsonResult generated
        })) |> ignore

        app.MapPost("/api/suggestions/{id}/confirm", Func<FeedbackService, string, _>(fun feedback id -> task {
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid suggestion id" |})
            | true, guid ->
                let! ok = feedback.ConfirmSuggestionAsync guid
                return (if ok then Results.Ok({| confirmed = true |}) else Results.NotFound())
        })) |> ignore

        app.MapPost("/api/suggestions/{id}/reject", Func<FeedbackService, string, _>(fun feedback id -> task {
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid suggestion id" |})
            | true, guid ->
                let! ok = feedback.RejectSuggestionAsync guid
                return (if ok then Results.Ok({| rejected = true |}) else Results.NotFound())
        })) |> ignore

        // ─── Candidate workspace: test confirmed improvements, then upgrade ───

        // Build a sandbox workspace ("candidate") from all confirmed suggestions so the
        // user can start a test session against it (WorkspaceKey = "candidate").
        app.MapPost("/api/candidate/build", Func<FeedbackService, IWorkspaceRegistry, _>(fun feedback registry -> task {
            let! confirmed = buildCandidate feedback registry
            return Results.Ok({| workspaceKey = "candidate"; improvements = List.length confirmed |})
        })) |> ignore

        // Promote the confirmed improvements into the live system permanently.
        app.MapPost("/api/candidate/upgrade", Func<FeedbackService, IWorkspaceRegistry, _>(fun feedback registry -> task {
            let! results = upgradeCandidate feedback registry
            return Results.Ok({| upgraded = List.length results; results = results |})
        })) |> ignore

    /// Start a standalone host exposing ONLY the enhancement-loop endpoints
    /// (no Orleans silo, no LLM). Intended for integration tests. Returns the
    /// running WebApplication so the caller can stop it.
    let startEnhancementHost (workspaceRoot: string) (feedbackDir: string) (port: int) : WebApplication =
        let builder = WebApplication.CreateBuilder([||])
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls(sprintf "http://127.0.0.1:%d" port) |> ignore
        registerEnhancementServices builder.Services workspaceRoot feedbackDir
        let app = builder.Build()
        mapEnhancementEndpoints app workspaceRoot
        app.StartAsync().GetAwaiter().GetResult()
        app

    /// Start the embedded server on a background thread. Returns the base URL.
    let start (settings: AppSettings) : string =
        let port = 5000
        let baseUrl = sprintf "http://localhost:%d" port

        Database.initialize ()

        let tcs = TaskCompletionSource<unit>()
        let cancellation = new CancellationTokenSource()
        cts <- Some cancellation

        Task.Factory.StartNew((fun () ->
            try
                let builder = WebApplication.CreateBuilder([||])

                builder.Host.UseOrleans(fun (siloBuilder: ISiloBuilder) ->
                    siloBuilder
                        .UseLocalhostClustering()
                        .AddAdoNetGrainStorage("sessionStore", fun (opts: Orleans.Configuration.AdoNetGrainStorageOptions) ->
                            opts.Invariant <- "System.Data.SQLite"
                            opts.ConnectionString <- Database.connectionString)
                        .Configure<ClusterOptions>(fun (opts: ClusterOptions) ->
                            opts.ClusterId <- "nao-desktop"
                            opts.ServiceId <- "nao-desktop")
                    |> ignore)
                |> ignore

                let workspaceRoot =
                    let envPath = Environment.GetEnvironmentVariable("NAO_WORKSPACE")
                    if String.IsNullOrEmpty(envPath) then
                        Path.Combine(AppContext.BaseDirectory, ".nao")
                        |> fun p -> Path.GetFullPath(Path.Combine(p, ".."))
                    else envPath

                let model = if String.IsNullOrWhiteSpace(settings.Provider.Model) then "llama3.2" else settings.Provider.Model
                let endpoint = if String.IsNullOrWhiteSpace(settings.Provider.Endpoint) then "http://localhost:11434" else settings.Provider.Endpoint

                builder.Services.AddSingleton<ILlmProvider>(fun _ ->
                    let config = { OllamaConfig.Default with Model = model; BaseUrl = endpoint }
                    ProviderFactory.create (ProviderType.Ollama config)) |> ignore

                builder.Services.AddSingleton<IWorkspaceRegistry>(fun _ ->
                    let registry = WorkspaceRegistry()
                    registry.Register(WorkspaceId.defaultId, loadMergedWorkspace workspaceRoot)
                    registry :> IWorkspaceRegistry) |> ignore

                builder.Services.AddSingleton<IOrchestratorFactory>(fun _ ->
                    AssistantOrchestratorFactory(fun req -> confirmationHandler req) :> IOrchestratorFactory) |> ignore

                // Conversation history persistence — file-based, grouped by session ID
                let conversationsDir = Path.Combine(Database.dataDir, "conversations")
                builder.Services.AddSingleton<IConversationStore>(fun _ ->
                    FileConversationStore(conversationsDir) :> IConversationStore) |> ignore

                // Observability + governance services injected into the SessionGrain harness.
                // File-backed here for the desktop demo; swap to PersistenceMode.Database
                // (or .InMemory for tests) to change where metrics/traces/audit/journal go.
                let observabilityDir = Path.Combine(Database.dataDir, "observability")
                builder.Services.AddSingleton<IHarnessServices>(fun _ ->
                    Persistence.harnessServices (PersistenceMode.File observabilityDir)) |> ignore

                // Feedback & adjust system — records turns, captures user feedback,
                // and persists versioned tool patches that are overlaid at load time.
                let feedbackDir = Path.Combine(Database.dataDir, "feedback")
                builder.Services.AddSingleton<FeedbackService>(fun _ ->
                    FeedbackService.File feedbackDir) |> ignore

                let app = builder.Build()
                app.UseWebSockets() |> ignore

                app.MapPost("/api/sessions", Func<HttpContext, IGrainFactory, _>(fun ctx grainFactory -> task {
                    let! request = ctx.Request.ReadFromJsonAsync<SessionStartRequest>()
                    let userId = Environment.UserName
                    let sessionId = Guid.NewGuid().ToString("N").[..7]
                    let grainKey = sprintf "%s/%s" userId sessionId

                    let session = grainFactory.GetGrain<ISessionGrain>(grainKey)
                    let startOpts = SessionStartOptions()
                    startOpts.AgentName <- request.AgentName
                    startOpts.WorkspaceKey <- request.WorkspaceKey
                    startOpts.ToolNames <- ResizeArray(request.ToolNames)

                    let! started = session.StartAsync(startOpts)
                    if started then
                        // Register in session directory for discoverability after restart
                        let directory = grainFactory.GetGrain<ISessionDirectoryGrain>(userId)
                        let entry = SessionDirectoryEntry()
                        entry.SessionId <- grainKey
                        entry.AgentName <- request.AgentName
                        entry.Title <- sprintf "%s session" request.AgentName
                        entry.CreatedAt <- DateTimeOffset.UtcNow
                        entry.LastActiveAt <- DateTimeOffset.UtcNow
                        entry.IsActive <- true
                        do! directory.RegisterAsync(entry)
                        return Results.Ok({| sessionId = grainKey |})
                    else
                        return Results.BadRequest({| error = "Failed to start session" |})
                })) |> ignore

                app.MapGet("/api/sessions", Func<IGrainFactory, _>(fun grainFactory -> task {
                    let userId = Environment.UserName
                    let directory = grainFactory.GetGrain<ISessionDirectoryGrain>(userId)
                    let! entries = directory.ListAllAsync()
                    let dtos =
                        entries
                        |> Array.map (fun e ->
                            {| sessionId = e.SessionId
                               agentName = e.AgentName
                               title = e.Title
                               createdAt = e.CreatedAt
                               lastActiveAt = e.LastActiveAt
                               isActive = e.IsActive |})
                    return Results.Ok(dtos)
                })) |> ignore

                app.MapGet("/api/sessions/history/{**id}", Func<IGrainFactory, string, _>(fun grainFactory id -> task {
                    let session = grainFactory.GetGrain<ISessionGrain>(id)
                    let! history = session.GetHistoryAsync()
                    let dtos =
                        history
                        |> Array.map (fun m ->
                            { MessageDto.Role = m.Role.ToLowerInvariant()
                              Content = m.Content })
                    return Results.Ok(dtos)
                })) |> ignore

                app.MapPost("/api/sessions/feedback/{**id}", Func<HttpContext, IGrainFactory, string, _>(fun ctx grainFactory id -> task {
                    let! request = ctx.Request.ReadFromJsonAsync<FeedbackRequest>()
                    let session = grainFactory.GetGrain<ISessionGrain>(id)
                    let! rationales = session.SubmitFeedbackAsync(request.Sentiment, request.Comment)
                    return Results.Ok({| proposals = rationales |})
                })) |> ignore

                // Feedback / annotation / version / suggestion / candidate / register
                // endpoints — shared with the standalone enhancement test host.
                mapEnhancementEndpoints app workspaceRoot

                app.Map("/ws/sessions/{**id}", Func<HttpContext, IGrainFactory, string, _>(fun ctx grainFactory id -> task {
                    if ctx.WebSockets.IsWebSocketRequest then
                        do! handleWebSocket ctx grainFactory id
                        return Results.Empty
                    else
                        return Results.BadRequest({| error = "WebSocket connection required" |})
                })) |> ignore

                host <- Some app
                app.StartAsync(cancellation.Token).GetAwaiter().GetResult()
                tcs.TrySetResult() |> ignore

                // Block this background thread until shutdown is requested.
                app.WaitForShutdownAsync(cancellation.Token).GetAwaiter().GetResult()
            with ex ->
                tcs.TrySetException(ex) |> ignore
        ), cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default) |> ignore

        // Wait for host startup completion and verify the listener is reachable.
        let startupCompleted = tcs.Task.Wait(TimeSpan.FromSeconds(20.0))
        if not startupCompleted then
            failwith "Embedded server startup timed out after 20 seconds"

        waitForListener port (TimeSpan.FromSeconds(8.0))
        baseUrl

    /// Gracefully stop the embedded server.
    let stop () =
        // Cancel first to abort any in-flight requests
        match cts with
        | Some c ->
            c.Cancel()
            c.Dispose()
            cts <- None
        | None -> ()

        match host with
        | Some app ->
            try
                // Use a very short timeout — for an embedded local silo there's
                // nothing to gracefully hand off to.
                app.StopAsync(TimeSpan.FromMilliseconds(500.0)).Wait(1000) |> ignore
            with _ -> ()
            host <- None
        | None -> ()
        
        // Force exit the process to avoid Orleans silo lingering
        Environment.Exit(0)

    /// Restart the server with new settings (e.g. after provider/model change).
    let restart (settings: AppSettings) =
        // Stop existing server (without Environment.Exit)
        match cts with
        | Some c ->
            c.Cancel()
            c.Dispose()
            cts <- None
        | None -> ()

        match host with
        | Some app ->
            try app.StopAsync(TimeSpan.FromMilliseconds(500.0)).Wait(1000) |> ignore
            with _ -> ()
            host <- None
        | None -> ()

        // Start fresh
        start settings |> ignore
