namespace Nao.Feedback

open System
open Nao.Agents

/// Whether the user was satisfied with a turn.
[<RequireQualifiedAccess>]
type FeedbackSentiment =
    | Positive
    | Negative
    | Neutral

/// A single tool invocation captured during a turn.
type ToolCallRecord =
    { /// Tool name as invoked.
      Name: string
      /// Tool version that was used, if known.
      Version: string option
      /// Input passed to the tool.
      Input: string
      /// Output the tool produced.
      Output: string
      /// Where the tool came from (so adjustments can target the source).
      Provenance: ToolProvenance option }

/// A single sub-agent delegation captured during a turn.
type SubAgentCallRecord =
    { /// Sub-agent name.
      Name: string
      /// Input delegated to the sub-agent.
      Input: string
      /// Result returned by the sub-agent.
      Output: string }

/// A complete record of one orchestration turn: the user prompt, the agent and
/// tools that ran, and the final answer. This is the unit feedback is attached to.
type TurnRecord =
    { /// Stable identifier for this turn.
      TurnId: string
      /// Session this turn belongs to.
      SessionId: string
      /// User who initiated the turn.
      UserId: string
      /// Workspace the turn ran against.
      WorkspaceKey: string
      /// Agent that handled the turn.
      AgentName: string
      /// Agent version that handled the turn, if pinned.
      AgentVersion: string option
      /// The user's prompt.
      Input: string
      /// The agent's final answer.
      Output: string
      /// Tools invoked during the turn, in order.
      ToolCalls: ToolCallRecord list
      /// Sub-agents delegated to during the turn, in order.
      SubAgentCalls: SubAgentCallRecord list
      /// When the turn completed.
      CreatedAt: DateTimeOffset }

    static member Empty =
        { TurnId = ""
          SessionId = ""
          UserId = ""
          WorkspaceKey = ""
          AgentName = ""
          AgentVersion = None
          Input = ""
          Output = ""
          ToolCalls = []
          SubAgentCalls = []
          CreatedAt = DateTimeOffset.MinValue }

/// User feedback attached to a turn. The signal that drives adjustments.
type Feedback =
    { /// Unique identifier for this feedback entry.
      Id: Guid
      /// Turn this feedback refers to.
      TurnId: string
      /// Session the turn belonged to.
      SessionId: string
      /// User who gave the feedback.
      UserId: string
      /// Positive / negative / neutral.
      Sentiment: FeedbackSentiment
      /// Optional free-text explanation from the user.
      Comment: string option
      /// When the feedback was given.
      CreatedAt: DateTimeOffset
      /// Arbitrary extra context.
      Metadata: Map<string, string> }

/// Whether an annotation targets a tool or an agent.
[<RequireQualifiedAccess>]
type AnnotationKind =
    | Tool
    | Agent

/// Lifecycle of an annotation. Active annotations are overlaid onto their target at
/// load time; Disabled ones are retained (for history / re-enable) but not applied.
/// Dropping an annotation entirely restores the legacy definition.
[<RequireQualifiedAccess>]
type AnnotationStatus =
    | Active
    | Disabled

/// A persistent, dynamic adjustment ("annotation") layered onto a tool or agent at
/// runtime. Annotations never modify the legacy definition — they are overlays — so
/// disabling or removing one restores the original behaviour. They survive restarts
/// (persisted by the stores) and are reused on every load.
type Annotation =
    { /// Unique identifier.
      Id: Guid
      /// Whether this annotates a tool or an agent.
      Kind: AnnotationKind
      /// Name of the tool/agent this applies to.
      TargetName: string
      /// Version of the target this builds on (None = the base/unversioned target).
      BaseVersion: string option
      /// Active = applied at runtime; Disabled = retained but not applied.
      Status: AnnotationStatus
      /// Replace the target's description entirely (tools and agents).
      DescriptionOverride: string option
      /// Append guidance to the target's description (tools).
      DescriptionAppend: string option
      /// Prepend literal text to every tool input before it runs (tools only).
      InputPrefix: string option
      /// Append literal text to every tool output after it runs (tools only).
      OutputSuffix: string option
      /// Extra guidance injected into the agent's constraints (agents only).
      GuidanceAppend: string option
      /// Where the annotation came from: "feedback:<id>" or "manual".
      Source: string
      /// Provenance of the target (carried so loaders know how to materialise versions).
      Provenance: ToolProvenance option
      /// Why this annotation was created.
      Reason: string option
      /// When it was created.
      CreatedAt: DateTimeOffset }

    static member private New(kind: AnnotationKind, targetName: string) =
        { Id = Guid.NewGuid()
          Kind = kind
          TargetName = targetName
          BaseVersion = None
          Status = AnnotationStatus.Active
          DescriptionOverride = None
          DescriptionAppend = None
          InputPrefix = None
          OutputSuffix = None
          GuidanceAppend = None
          Source = "manual"
          Provenance = None
          Reason = None
          CreatedAt = DateTimeOffset.UtcNow }

    /// A blank active annotation targeting a tool; callers fill in the overrides they want.
    static member ForTool(targetName: string) =
        Annotation.New(AnnotationKind.Tool, targetName)

    /// A blank active annotation targeting an agent.
    static member ForAgent(targetName: string) =
        Annotation.New(AnnotationKind.Agent, targetName)

/// A proposed annotation produced by analysing feedback against a turn.
type AdjustmentProposal =
    { /// The annotation that would be applied.
      Annotation: Annotation
      /// Human-readable explanation of why this change is proposed.
      Rationale: string }

/// Lifecycle of a generated tool/agent version produced from annotations.
[<RequireQualifiedAccess>]
type VersionStatus =
    | Draft        // generated, awaiting user review
    | Active       // reviewed and confirmed — available for use
    | Deprecated   // superseded or rejected — retained but not offered by default

/// A generated tool/agent version, tracked through review. Created by "promoting" one
/// or more annotations into a concrete, versioned artifact that the user can review,
/// confirm (optionally replacing the legacy version), or deprecate.
type VersionRecord =
    { /// Unique identifier.
      Id: Guid
      /// Whether this is a tool or an agent version.
      Kind: AnnotationKind
      /// Name of the tool/agent.
      TargetName: string
      /// Version label produced (e.g. "v2").
      Version: string
      /// Draft / Active / Deprecated.
      Status: VersionStatus
      /// Annotations this version was generated from.
      SourceAnnotationIds: Guid list
      /// Materialised definition file path, if one was written.
      Location: string option
      /// Free-text notes (rationale, review comments).
      Notes: string option
      /// When it was created.
      CreatedAt: DateTimeOffset }

    static member Create(kind: AnnotationKind, targetName: string, version: string) =
        { Id = Guid.NewGuid()
          Kind = kind
          TargetName = targetName
          Version = version
          Status = VersionStatus.Draft
          SourceAnnotationIds = []
          Location = None
          Notes = None
          CreatedAt = DateTimeOffset.UtcNow }

/// Where a feedback signal originated. Explicit feedback is an intentional good/bad
/// rating; conversation feedback is inferred heuristically from the chat history;
/// memory feedback is surfaced by the memory system. The source is stored on each
/// `Feedback` in its `Metadata` (see the `FeedbackSource` module) so the cross-session
/// aggregator can weigh and explain its suggestions without breaking existing literals.
module FeedbackSource =
    /// Metadata key under which the source marker is stored on a `Feedback`.
    [<Literal>]
    let Key = "source"

    [<Literal>]
    let Explicit = "explicit"

    [<Literal>]
    let Conversation = "conversation"

    [<Literal>]
    let Memory = "memory"

    /// Read the source marker from a feedback entry (defaults to explicit).
    let ofFeedback (f: Feedback) : string =
        match f.Metadata.TryFind Key with
        | Some v -> v
        | None -> Explicit

    /// Stamp a source marker into a metadata map.
    let stamp (source: string) (metadata: Map<string, string>) : Map<string, string> =
        metadata |> Map.add Key source

/// Lifecycle of a cross-session improvement suggestion. Suggestions are the review gate:
/// feedback (explicit + implicit) is aggregated into Proposed suggestions; the user
/// Confirms or Rejects them; a Confirmed suggestion is later materialised and, once tested,
/// marked Applied.
[<RequireQualifiedAccess>]
type SuggestionStatus =
    | Proposed     // generated from feedback, awaiting user review
    | Confirmed    // user approved — eligible for candidate-workspace testing
    | Rejected     // user dismissed — kept for history, never applied
    | Applied      // promoted into a version / persisted into the live workspace

/// A cross-session improvement proposal produced by analysing all stored feedback. Unlike
/// an `Annotation` (an immediate, per-target live overlay), a `Suggestion` is review-gated:
/// it aggregates the supporting feedback across sessions, proposes a concrete change (an
/// improvement overlay for an existing target, or a brand-new tool/agent definition), and
/// is only materialised once the user confirms it.
type Suggestion =
    { /// Unique identifier.
      Id: Guid
      /// Whether the suggestion targets a tool or an agent.
      Kind: AnnotationKind
      /// Name of the tool/agent to improve, or the proposed name of a new artifact.
      TargetName: string
      /// True when proposing a brand-new tool/agent rather than improving an existing one.
      IsNew: bool
      /// The improvement overlay to apply to an existing target (None for new artifacts).
      ProposedAnnotation: Annotation option
      /// Raw JSON definition for a proposed new tool/agent (None when improving).
      ProposedDefinition: string option
      /// Human-readable explanation of the proposed change.
      Rationale: string
      /// Feedback entries that motivated this suggestion.
      SupportingFeedbackIds: Guid list
      /// Distinct sessions the supporting feedback came from.
      SupportingSessions: string list
      /// How many negative signals back this suggestion (used for ranking).
      NegativeCount: int
      /// Proposed / Confirmed / Rejected / Applied.
      Status: SuggestionStatus
      /// Version produced when the suggestion was materialised, if any.
      ResultVersionId: Guid option
      /// When the suggestion was generated.
      CreatedAt: DateTimeOffset }

    /// A Proposed suggestion to improve an existing tool/agent.
    static member Improve(kind: AnnotationKind, targetName: string) =
        { Id = Guid.NewGuid()
          Kind = kind
          TargetName = targetName
          IsNew = false
          ProposedAnnotation = None
          ProposedDefinition = None
          Rationale = ""
          SupportingFeedbackIds = []
          SupportingSessions = []
          NegativeCount = 0
          Status = SuggestionStatus.Proposed
          ResultVersionId = None
          CreatedAt = DateTimeOffset.UtcNow }
