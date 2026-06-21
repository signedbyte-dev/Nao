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
          ToolNames = [ "create_folder"; "write_file"; "read_file"; "list_folder"; "delete"; "get_datetime"; "calculator"; "http_request"; "web_fetch"; "search_files"; "find_files"; "convert_document" ]
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

/// One step of the process an agent ran during a turn (a tool invocation or a
/// sub-agent delegation), surfaced so a frontend can show the whole process.
[<CLIMutable>]
type TurnStepDto =
    { /// "tool" | "agent".
      [<JsonPropertyName("kind")>]
      Kind: string
      /// Display title — typically the tool or sub-agent name.
      [<JsonPropertyName("title")>]
      Title: string
      /// Input passed to the tool / sub-agent.
      [<JsonPropertyName("input")>]
      Input: string
      /// Output the tool / sub-agent produced.
      [<JsonPropertyName("output")>]
      Output: string }

/// Envelope for a live progress event pushed over the WebSocket while a turn is being
/// processed: the steps completed so far. Lets the UI show "what's been done" in real time.
[<CLIMutable>]
type StepsEventDto =
    { [<JsonPropertyName("steps")>]
      Steps: TurnStepDto[] }

[<CLIMutable>]
type MessageDto =
    { [<JsonPropertyName("role")>]
      Role: string
      [<JsonPropertyName("content")>]
      Content: string
      /// Turn this message belongs to ("" for legacy messages).
      [<JsonPropertyName("turnId")>]
      TurnId: string
      /// Process steps for an assistant turn (empty for user / legacy messages).
      [<JsonPropertyName("steps")>]
      Steps: TurnStepDto[]
      /// Names of files attached to this message (content is not transmitted back).
      [<JsonPropertyName("attachments")>]
      Attachments: string[] }

/// A file attached to a chat message. `content` carries the file body to the agent
/// on the way in; it is intentionally not persisted into the rendered transcript.
[<CLIMutable>]
type AttachmentDto =
    { [<JsonPropertyName("name")>]
      Name: string
      [<JsonPropertyName("content")>]
      Content: string }

/// Structured chat message: the user's typed text plus any attached files. The server
/// embeds the attachment content into the LLM prompt but stores only the text and the
/// attachment names, so the UI renders tags rather than the raw file content.
[<CLIMutable>]
type ChatMessageRequest =
    { [<JsonPropertyName("text")>]
      Text: string
      [<JsonPropertyName("attachments")>]
      Attachments: AttachmentDto[] }


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

/// A tool or agent available in the workspace, surfaced to the UI.
[<CLIMutable>]
type DefinitionInfoDto =
    { [<JsonPropertyName("name")>]
      Name: string
      [<JsonPropertyName("description")>]
      Description: string
      /// "code" | "json" for tools; "json" | "builtin" for agents.
      [<JsonPropertyName("source")>]
      Source: string }

/// Request to generate a tool or agent definition from a natural-language requirement.
[<CLIMutable>]
type GenerateRequest =
    { [<JsonPropertyName("requirement")>]
      Requirement: string }

/// A generated definition the user can review before saving.
[<CLIMutable>]
type GeneratedDefinitionDto =
    { [<JsonPropertyName("name")>]
      Name: string
      /// Pretty-printed JSON of the generated definition.
      [<JsonPropertyName("json")>]
      Json: string }

/// A knowledge file stored in the workspace knowledge folder.
[<CLIMutable>]
type KnowledgeFileDto =
    { [<JsonPropertyName("name")>]
      Name: string
      [<JsonPropertyName("sizeBytes")>]
      SizeBytes: int64
      [<JsonPropertyName("chunks")>]
      Chunks: int }

/// Upload a knowledge file (UTF-8 text content).
[<CLIMutable>]
type KnowledgeUploadRequest =
    { [<JsonPropertyName("name")>]
      Name: string
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
