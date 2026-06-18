namespace Nao.Demo

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
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
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

module DemoTools =

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
        let dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nao.Desktop")
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
                    let workspace = WorkspaceLoader.loadWorkspace workspaceRoot
                    let merged = { workspace with Tools = workspace.Tools @ DemoTools.allTools }
                    let registry = WorkspaceRegistry()
                    registry.Register(WorkspaceId.defaultId, merged)
                    registry :> IWorkspaceRegistry) |> ignore

                builder.Services.AddSingleton<IOrchestratorFactory>(fun _ ->
                    DemoOrchestratorFactory(fun req -> confirmationHandler req) :> IOrchestratorFactory) |> ignore

                // Conversation history persistence — file-based, grouped by session ID
                let conversationsDir = Path.Combine(Database.dataDir, "conversations")
                builder.Services.AddSingleton<IConversationStore>(fun _ ->
                    FileConversationStore(conversationsDir) :> IConversationStore) |> ignore

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
