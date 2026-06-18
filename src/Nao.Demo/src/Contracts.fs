namespace Nao.Demo

open System
open System.Text.Json.Serialization

/// Shared DTOs for the Nao Demo API.
/// Used by Server, CLI, and Desktop.

// === HTTP (session creation only) ===

[<CLIMutable>]
type SessionStartRequest =
    { [<JsonPropertyName("agentName")>]
      AgentName: string
      [<JsonPropertyName("toolNames")>]
      ToolNames: string list
      [<JsonPropertyName("workspaceKey")>]
      WorkspaceKey: string }

    static member Default =
        { AgentName = "nao-assistant"
          ToolNames = [ "create_folder"; "write_file"; "read_file"; "list_folder"; "delete"; "get_datetime"; "calculator" ]
          WorkspaceKey = "default" }

[<CLIMutable>]
type SessionInfoDto =
    { [<JsonPropertyName("sessionId")>]
      SessionId: string
      [<JsonPropertyName("agentName")>]
      AgentName: string
      [<JsonPropertyName("workspaceKey")>]
      WorkspaceKey: string
      [<JsonPropertyName("activeConversation")>]
      ActiveConversation: string
      [<JsonPropertyName("isActive")>]
      IsActive: bool
      [<JsonPropertyName("createdAt")>]
      CreatedAt: DateTimeOffset
      [<JsonPropertyName("lastActiveAt")>]
      LastActiveAt: DateTimeOffset }

[<CLIMutable>]
type MessageDto =
    { [<JsonPropertyName("role")>]
      Role: string
      [<JsonPropertyName("content")>]
      Content: string }

[<CLIMutable>]
type ErrorResponse =
    { [<JsonPropertyName("error")>]
      Error: string }

[<CLIMutable>]
type SessionListEntry =
    { [<JsonPropertyName("sessionId")>]
      SessionId: string
      [<JsonPropertyName("agentName")>]
      AgentName: string
      [<JsonPropertyName("title")>]
      Title: string
      [<JsonPropertyName("createdAt")>]
      CreatedAt: DateTimeOffset
      [<JsonPropertyName("lastActiveAt")>]
      LastActiveAt: DateTimeOffset
      [<JsonPropertyName("isActive")>]
      IsActive: bool }

// === WebSocket Protocol ===
// Client sends WsRequest, Server sends WsResponse
// All frames are JSON with a "type" string field

[<CLIMutable>]
type WsRequest =
    { [<JsonPropertyName("type")>]
      Type: string
      [<JsonPropertyName("payload")>]
      Payload: string }

[<CLIMutable>]
type WsResponse =
    { [<JsonPropertyName("type")>]
      Type: string
      [<JsonPropertyName("payload")>]
      Payload: string }

/// Known request type constants
[<RequireQualifiedAccess>]
module WsRequestType =
    [<Literal>]
    let Chat = "chat"
    [<Literal>]
    let Info = "info"
    [<Literal>]
    let History = "history"
    [<Literal>]
    let Clear = "clear"
    [<Literal>]
    let Conversations = "conversations"
    [<Literal>]
    let Switch = "switch"

/// Known response type constants
[<RequireQualifiedAccess>]
module WsResponseType =
    [<Literal>]
    let Chunk = "chunk"
    [<Literal>]
    let Done = "done"
    [<Literal>]
    let Info = "info"
    [<Literal>]
    let History = "history"
    [<Literal>]
    let Conversations = "conversations"
    [<Literal>]
    let Error = "error"
    [<Literal>]
    let Event = "event"
