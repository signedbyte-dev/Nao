namespace Nao.Core

open System.Threading
open System.Threading.Tasks

/// Ambient, async-flowed context describing the session and turn a piece of work is
/// running inside. The runtime (the session grain) sets this before executing a turn so
/// that downstream code — in particular tools that produce files or spawn background
/// tasks — can attribute their output to the right session and turn without threading
/// the identity through every call site.
module SessionExecution =

    /// A request to launch a background task. `Params` is an arbitrary, serializable
    /// key/value bag the task executor (keyed by `Kind`) knows how to interpret — e.g.
    /// for "document-conversion": source/target/media types; for "agent": agent/input.
    /// It is serialized to JSON so the owning task grain can persist and replay it.
    type TaskSpec =
        { /// Executor kind (e.g. "document-conversion", "agent").
          Kind: string
          /// Human-readable task title shown in the UI.
          Title: string
          /// Serializable parameters interpreted by the executor for this kind.
          Params: Map<string, string> }

    /// The session/turn a unit of work belongs to.
    type SessionScope =
        { /// Grain key: "userId/sessionId".
          SessionKey: string
          /// Session key whose file folder backs file operations in this scope. Usually the
          /// same as SessionKey, but a task sub-session points this at its parent session so
          /// the user's attachments and the files the task generates live in one shared
          /// folder visible from the conversation.
          FilesKey: string
          /// Names of agents flagged async. When the orchestrator delegates to one of these,
          /// it spawns a background task (a sub-session) instead of running it inline.
          AsyncAgents: Set<string>
          /// Id of the turn currently being processed ("" when none).
          TurnId: string
          /// Launch a background task owned by this session, returning its task id.
          /// Supplied by the runtime (the session grain); the default is a no-op that
          /// signals "no async task host available" by returning an empty id.
          SpawnTask: TaskSpec -> Task<string> }

    let private current_ = AsyncLocal<SessionScope option>()

    /// The scope flowing on the current async context, if any.
    let current () : SessionScope option = current_.Value

    /// Set the ambient scope for the current async flow (and everything it awaits).
    let set (scope: SessionScope) = current_.Value <- Some scope

    /// Clear the ambient scope.
    let clear () = current_.Value <- None
