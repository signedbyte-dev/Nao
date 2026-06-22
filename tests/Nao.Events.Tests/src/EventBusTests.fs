namespace Nao.Events.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Feedback
open Nao.Events

/// Test consumer that records the events it receives (and can optionally throw).
type private RecordingConsumer(?fail: bool) =
    let received = ResizeArray<NaoEvent>()
    let shouldFail = defaultArg fail false
    member _.Received = received
    interface IEventConsumer with
        member _.HandleAsync(evt) =
            if shouldFail then failwith "boom"
            received.Add evt
            Task.CompletedTask

[<TestClass>]
type EventBusTests() =

    let scope () =
        EventScope.Create(
            userId = "dev",
            sessionId = "s1",
            conversationId = "c1",
            workspaceKey = "ws",
            actionId = "turn-1",
            sessionKey = "dev/s1")

    let sampleTurn () =
        { TurnRecord.Empty with
            TurnId = "turn-1"
            SessionId = "s1"
            UserId = "dev"
            Input = "hello"
            Output = "hi"
            CreatedAt = DateTimeOffset.UtcNow }

    let sampleFeedback () =
        { Id = Guid.NewGuid()
          TurnId = "turn-1"
          SessionId = "s1"
          UserId = "dev"
          Sentiment = FeedbackSentiment.Positive
          Comment = Some "great"
          CreatedAt = DateTimeOffset.UtcNow
          Metadata = Map.empty }

    let tempDir () =
        let dir = Path.Combine(Path.GetTempPath(), "nao-events-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        dir

    [<TestMethod>]
    member _.PublishAsync_FansOutToAllConsumers() =
        let bus = InMemoryEventBus() :> IEventBus
        let a = RecordingConsumer()
        let b = RecordingConsumer()
        bus.Subscribe(a :> IEventConsumer)
        bus.Subscribe(b :> IEventConsumer)

        bus.PublishAsync(TurnCompleted(scope (), sampleTurn ())).Wait()

        Assert.AreEqual(1, a.Received.Count)
        Assert.AreEqual(1, b.Received.Count)

    [<TestMethod>]
    member _.PublishAsync_IsolatesAFailingConsumer() =
        let bus = InMemoryEventBus() :> IEventBus
        let failing = RecordingConsumer(fail = true)
        let healthy = RecordingConsumer()
        bus.Subscribe(failing :> IEventConsumer)
        bus.Subscribe(healthy :> IEventConsumer)

        // Must not throw even though one consumer fails...
        bus.PublishAsync(TurnCompleted(scope (), sampleTurn ())).Wait()

        // ...and the healthy consumer still receives the event.
        Assert.AreEqual(1, healthy.Received.Count)

    [<TestMethod>]
    member _.SessionStrategy_RoutesTurnToPerSessionFolder() =
        let root = tempDir ()
        let strategy : IFeedbackStorageStrategy =
            SessionFeedbackStrategy(fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))
        let evt = TurnCompleted(scope (), sampleTurn ())

        (strategy :> IEventConsumer).HandleAsync(evt).Wait()

        let expected = Path.Combine(root, "dev_s1", "feedback", "turns.jsonl")
        Assert.IsTrue(File.Exists expected, sprintf "expected turns file at %s" expected)

    [<TestMethod>]
    member _.SessionStrategy_RoutesImplicitFeedbackToPerSessionFolder() =
        let root = tempDir ()
        let strategy : IFeedbackStorageStrategy =
            SessionFeedbackStrategy(fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))
        let evt = ImplicitFeedbackCaptured(scope (), sampleFeedback ())

        (strategy :> IEventConsumer).HandleAsync(evt).Wait()

        let expected = Path.Combine(root, "dev_s1", "feedback", "feedback.jsonl")
        Assert.IsTrue(File.Exists expected, sprintf "expected feedback file at %s" expected)

    [<TestMethod>]
    member _.SessionStrategy_SeparatesDistinctSessions() =
        let root = tempDir ()
        let strategy : IFeedbackStorageStrategy =
            SessionFeedbackStrategy(fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))
        let scopeA =
            EventScope.Create("dev", "s1", "c1", "ws", "turn-a", "dev/s1")
        let scopeB =
            EventScope.Create("dev", "s2", "c1", "ws", "turn-b", "dev/s2")

        (strategy :> IEventConsumer).HandleAsync(TurnCompleted(scopeA, sampleTurn ())).Wait()
        (strategy :> IEventConsumer).HandleAsync(TurnCompleted(scopeB, sampleTurn ())).Wait()

        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s1", "feedback", "turns.jsonl")))
        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s2", "feedback", "turns.jsonl")))

    [<TestMethod>]
    member _.CategoryStrategy_RoutesAllSessionsToSharedFolder() =
        let root = tempDir ()
        let strategy : IFeedbackStorageStrategy = CategoryFeedbackStrategy(root)
        let scopeA =
            EventScope.Create("dev", "s1", "c1", "ws", "turn-a", "dev/s1")
        let scopeB =
            EventScope.Create("dev", "s2", "c1", "ws", "turn-b", "dev/s2")

        (strategy :> IEventConsumer).HandleAsync(TurnCompleted(scopeA, sampleTurn ())).Wait()
        (strategy :> IEventConsumer).HandleAsync(ImplicitFeedbackCaptured(scopeB, sampleFeedback ())).Wait()

        // Everything lands in ONE shared folder regardless of session key.
        Assert.IsTrue(File.Exists(Path.Combine(root, "turns.jsonl")))
        Assert.IsTrue(File.Exists(Path.Combine(root, "feedback.jsonl")))
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "dev_s1")))

    [<TestMethod>]
    member _.FeedbackFor_ReturnsServiceForReads() =
        let root = tempDir ()
        let strategy : IFeedbackStorageStrategy =
            SessionFeedbackStrategy(fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))

        let svc = strategy.FeedbackFor "dev/s1"

        Assert.IsNotNull(box svc)
