namespace Nao.Assistant

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

/// Feedback submitted for the most recent turn of a session.
[<CLIMutable>]
type FeedbackRequest =
    { [<JsonPropertyName("sentiment")>]
      Sentiment: string
      [<JsonPropertyName("comment")>]
      Comment: string }

/// Manually create an annotation (runtime overlay) for a tool or agent.
[<CLIMutable>]
type AnnotationRequest =
    { [<JsonPropertyName("kind")>]
      Kind: string                       // "tool" | "agent"
      [<JsonPropertyName("targetName")>]
      TargetName: string
      [<JsonPropertyName("baseVersion")>]
      BaseVersion: string                // "" = base/unversioned
      [<JsonPropertyName("descriptionOverride")>]
      DescriptionOverride: string
      [<JsonPropertyName("descriptionAppend")>]
      DescriptionAppend: string
      [<JsonPropertyName("inputPrefix")>]
      InputPrefix: string
      [<JsonPropertyName("outputSuffix")>]
      OutputSuffix: string
      [<JsonPropertyName("guidanceAppend")>]
      GuidanceAppend: string
      [<JsonPropertyName("reason")>]
      Reason: string }

/// Change an annotation's status.
[<CLIMutable>]
type AnnotationStatusRequest =
    { [<JsonPropertyName("status")>]
      Status: string }                   // "active" | "disabled"

/// Promote a target's annotations into a new Draft version.
[<CLIMutable>]
type PromoteVersionRequest =
    { [<JsonPropertyName("kind")>]
      Kind: string                       // "tool" | "agent"
      [<JsonPropertyName("targetName")>]
      TargetName: string
      [<JsonPropertyName("version")>]
      Version: string }                  // "" = auto-increment

/// Confirm a Draft version (optionally replacing the legacy version).
[<CLIMutable>]
type ConfirmVersionRequest =
    { [<JsonPropertyName("replaceLegacy")>]
      ReplaceLegacy: bool }

/// Register a user-supplied tool or agent definition. `definition` is the raw JSON
/// of the definition (same schema the workspace loader reads from `.nao`).
[<CLIMutable>]
type RegisterDefinitionRequest =
    { [<JsonPropertyName("name")>]
      Name: string
      [<JsonPropertyName("definition")>]
      Definition: System.Text.Json.JsonElement }

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

// === Feedback / suggestion enhancement loop response DTOs ===
// The annotation/version/suggestion list endpoints serialise the underlying F#
// domain records with the FeedbackJson converter; NaoClient deserialises those
// directly into the Nao.Feedback domain types. The candidate/upgrade endpoints,
// however, return plain ASP.NET JSON, so the result shape below is a simple DTO.

/// One entry in the result of permanently upgrading the live system.
[<CLIMutable>]
type UpgradeResultDto =
    { Target: string
      Kind: string
      Persisted: string
      Detail: string }

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
