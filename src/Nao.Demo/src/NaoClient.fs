namespace Nao.Demo

open System
open System.Buffers
open System.Net.Http
open System.Net.Http.Json
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Event raised when the server pushes a message.
[<RequireQualifiedAccess>]
type NaoEvent =
    | Chunk of text: string
    | Done of fullResponse: string
    | Info of SessionInfoDto
    | History of MessageDto list
    | Conversations of string list
    | Error of string
    | ServerEvent of string

/// Client for the Nao Demo Server using HTTP (session creation) + WebSocket (chat & commands).
type NaoClient(baseUrl: string) =
    let http = new HttpClient(BaseAddress = Uri(baseUrl))
    let jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    let mutable ws: ClientWebSocket option = None
    let mutable receiveTask: Task option = None
    let mutable cts = new CancellationTokenSource()

    let onMessage = Event<NaoEvent>()

    let wsUrl =
        let uri = Uri(baseUrl)
        let scheme = if uri.Scheme = "https" then "wss" else "ws"
        sprintf "%s://%s:%d" scheme uri.Host uri.Port

    let ensureSuccess (resp: HttpResponseMessage) = task {
        if not resp.IsSuccessStatusCode then
            let! body = resp.Content.ReadAsStringAsync()
            let error =
                try (JsonSerializer.Deserialize<ErrorResponse>(body, jsonOptions)).Error
                with _ -> body
            failwithf "API error (%d): %s" (int resp.StatusCode) error
    }

    let sendWs (msg: WsRequest) = task {
        match ws with
        | Some socket when socket.State = WebSocketState.Open ->
            let json = JsonSerializer.Serialize(msg, jsonOptions)
            let bytes = Encoding.UTF8.GetBytes(json)
            do! socket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Text, true, cts.Token)
        | _ -> failwith "WebSocket not connected"
    }

    let parseResponse (json: string) =
        try
            let resp = JsonSerializer.Deserialize<WsResponse>(json, jsonOptions)
            match resp.Type with
            | WsResponseType.Chunk -> Some (NaoEvent.Chunk resp.Payload)
            | WsResponseType.Done -> Some (NaoEvent.Done resp.Payload)
            | WsResponseType.Info ->
                let info = JsonSerializer.Deserialize<SessionInfoDto>(resp.Payload, jsonOptions)
                Some (NaoEvent.Info info)
            | WsResponseType.History ->
                let msgs = JsonSerializer.Deserialize<MessageDto[]>(resp.Payload, jsonOptions)
                Some (NaoEvent.History (Array.toList msgs))
            | WsResponseType.Conversations ->
                let convs = JsonSerializer.Deserialize<string[]>(resp.Payload, jsonOptions)
                Some (NaoEvent.Conversations (Array.toList convs))
            | WsResponseType.Error -> Some (NaoEvent.Error resp.Payload)
            | WsResponseType.Event -> Some (NaoEvent.ServerEvent resp.Payload)
            | _ -> None
        with _ -> None

    let receiveLoop () = task {
        let buffer = ArrayPool<byte>.Shared.Rent(8192)
        try
            let mutable looping = true
            while looping && ws.IsSome && ws.Value.State = WebSocketState.Open do
                let segments = ResizeArray<byte>()
                let mutable endOfMessage = false
                while not endOfMessage do
                    let! result = ws.Value.ReceiveAsync(ArraySegment(buffer), cts.Token)
                    if result.MessageType = WebSocketMessageType.Close then
                        do! ws.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                        endOfMessage <- true
                        looping <- false
                    else
                        segments.AddRange(buffer.[0..result.Count - 1])
                        endOfMessage <- result.EndOfMessage

                if looping && segments.Count > 0 then
                    let json = Encoding.UTF8.GetString(segments.ToArray())
                    match parseResponse json with
                    | Some evt -> onMessage.Trigger evt
                    | None -> ()
        with
        | :? OperationCanceledException -> ()
        | :? WebSocketException -> ()

        ArrayPool<byte>.Shared.Return(buffer)
    }

    /// Event stream for server-pushed messages.
    [<CLIEvent>]
    member _.OnMessage = onMessage.Publish

    /// Create a new session via HTTP, returns the session ID.
    member _.CreateSessionAsync(request: SessionStartRequest) : Task<string> = task {
        let! resp = http.PostAsJsonAsync("/api/sessions", request, jsonOptions)
        do! ensureSuccess resp
        let! result = resp.Content.ReadFromJsonAsync<{| sessionId: string |}>(jsonOptions)
        return result.sessionId
    }

    /// List all existing sessions via HTTP.
    member _.ListSessionsAsync() : Task<SessionListEntry list> = task {
        let! resp = http.GetAsync("/api/sessions")
        do! ensureSuccess resp
        let! entries = resp.Content.ReadFromJsonAsync<SessionListEntry[]>(jsonOptions)
        return entries |> Array.toList
    }

    /// Load full conversation history for a session via HTTP.
    member _.LoadSessionHistoryAsync(sessionId: string) : Task<MessageDto list> = task {
        let! resp = http.GetAsync(sprintf "/api/sessions/history/%s" sessionId)
        do! ensureSuccess resp
        let! entries = resp.Content.ReadFromJsonAsync<MessageDto[]>(jsonOptions)
        return entries |> Array.toList
    }

    /// Connect WebSocket to a session for bidirectional communication.
    member _.ConnectAsync(sessionId: string) : Task = task {
        let socket = new ClientWebSocket()
        let url = sprintf "%s/ws/sessions/%s" wsUrl sessionId
        do! socket.ConnectAsync(Uri(url), cts.Token)
        ws <- Some socket
        receiveTask <- Some (Task.Factory.StartNew(fun () -> receiveLoop().Wait()))
    }

    /// Send a chat message (response comes as Chunk/Done events).
    member _.SendChatAsync(message: string) : Task =
        sendWs { Type = WsRequestType.Chat; Payload = message }

    /// Request session info (response comes as Info event).
    member _.RequestInfoAsync() : Task =
        sendWs { Type = WsRequestType.Info; Payload = "" }

    /// Request conversation history (response comes as History event).
    member _.RequestHistoryAsync() : Task =
        sendWs { Type = WsRequestType.History; Payload = "" }

    /// Clear conversation history.
    member _.SendClearAsync() : Task =
        sendWs { Type = WsRequestType.Clear; Payload = "" }

    /// List conversations.
    member _.RequestConversationsAsync() : Task =
        sendWs { Type = WsRequestType.Conversations; Payload = "" }

    /// Switch conversation.
    member _.SendSwitchAsync(name: string) : Task =
        sendWs { Type = WsRequestType.Switch; Payload = name }

    /// Send a chat message and wait for the full response (blocking convenience method).
    member this.ChatAsync(sessionId: string, message: string) : Task<string> = task {
        if ws.IsNone || ws.Value.State <> WebSocketState.Open then
            do! this.ConnectAsync(sessionId)

        let tcs = TaskCompletionSource<string>()
        let handler = Handler<NaoEvent>(fun _ evt ->
            match evt with
            | NaoEvent.Done response -> tcs.TrySetResult(response) |> ignore
            | NaoEvent.Error err -> tcs.TrySetException(Exception(err)) |> ignore
            | _ -> ())

        this.OnMessage.AddHandler handler
        do! this.SendChatAsync(message)
        let! result = tcs.Task
        this.OnMessage.RemoveHandler handler
        return result
    }

    /// Disconnect WebSocket.
    member _.DisconnectAsync() : Task = task {
        match ws with
        | Some socket when socket.State = WebSocketState.Open ->
            do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
            ws <- None
        | _ -> ws <- None
    }

    interface IDisposable with
        member this.Dispose() =
            cts.Cancel()
            match ws with
            | Some socket -> try socket.Dispose() with _ -> ()
            | None -> ()
            http.Dispose()
            cts.Dispose()
