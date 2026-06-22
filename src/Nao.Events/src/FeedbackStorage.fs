namespace Nao.Events

open System.Collections.Concurrent
open System.Threading.Tasks
open Nao.Feedback

/// A storage strategy for feedback data. It is BOTH an event consumer (the write side — it
/// persists TurnCompleted / ImplicitFeedbackCaptured wherever it chooses) AND the provider
/// of the read/command FeedbackService for a session (the query side). Swapping the
/// strategy switches where ALL feedback data lives without touching any producer.
type IFeedbackStorageStrategy =
    inherit IEventConsumer
    /// The FeedbackService backing reads + synchronous commands for a session key.
    abstract member FeedbackFor: sessionKey: string -> FeedbackService

/// Session-based strategy: every session's feedback lives in its own folder, resolved by
/// `rootFor` (e.g. sessions/<key>/feedback). Services are memoised per resolved root so the
/// read side and the write side share one store.
type SessionFeedbackStrategy(rootFor: string -> string) =
    let services = ConcurrentDictionary<string, FeedbackService>()
    let serviceFor (sessionKey: string) =
        services.GetOrAdd(rootFor sessionKey, fun dir -> FeedbackService.File dir)

    interface IFeedbackStorageStrategy with
        member _.FeedbackFor(sessionKey: string) = serviceFor sessionKey
        member _.HandleAsync(evt: NaoEvent) : Task =
            match evt with
            | TurnCompleted(scope, turn) -> (serviceFor scope.SessionKey).RecordTurnAsync turn
            | ImplicitFeedbackCaptured(scope, fb) -> (serviceFor scope.SessionKey).SaveFeedbackAsync fb
            | _ -> Task.CompletedTask

/// Category-based strategy: all feedback (every session) shares one folder at `root` — the
/// legacy layout. Demonstrates that switching strategy needs no producer change.
type CategoryFeedbackStrategy(root: string) =
    let service = lazy (FeedbackService.File root)

    interface IFeedbackStorageStrategy with
        member _.FeedbackFor(_sessionKey: string) = service.Value
        member _.HandleAsync(evt: NaoEvent) : Task =
            match evt with
            | TurnCompleted(_, turn) -> service.Value.RecordTurnAsync turn
            | ImplicitFeedbackCaptured(_, fb) -> service.Value.SaveFeedbackAsync fb
            | _ -> Task.CompletedTask
