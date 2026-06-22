namespace Nao.Events

open System.Collections.Concurrent
open System.Threading.Tasks
open Nao.Core
open Nao.Agents

/// Internal helpers for building the event scope of an observability signal and publishing
/// it. The per-turn action id is taken from the ambient SessionExecution scope (set by the
/// session grain) so each signal is attributed to the turn that produced it.
module private Obs =

    let buildScope (sessionKey: string) : EventScope =
        // Prefer the ambient turn scope (it carries the precise turn id and, for a task
        // sub-session, the more specific key); fall back to the bundle's session key.
        let key, turnId =
            match SessionExecution.current () with
            | Some s -> s.SessionKey, s.TurnId
            | None -> sessionKey, ""
        let userId, sessionId =
            match key.IndexOf('/') with
            | i when i >= 0 -> key.Substring(0, i), key.Substring(i + 1)
            | _ -> key, key
        EventScope.Create(userId, sessionId, "", "", turnId, key)

    /// Fire-and-forget publish for the synchronous (unit-returning) sinks. Safe to ignore:
    /// reads always go to the wrapped backing store, and InMemoryEventBus isolates a failing
    /// consumer, so a subscriber can never break the producer's turn.
    let emit (bus: IEventBus) (sessionKey: string) (signal: ObservabilitySignal) =
        bus.PublishAsync(ObservabilityCaptured(buildScope sessionKey, signal)) |> ignore

/// Tee tracer: writes go to the real backing tracer (so span threading and GetTrace stay
/// correct) and ALSO publish a span signal to the bus. Reads delegate to the backing.
type private PublishingTracer(sessionKey: string, bus: IEventBus, inner: ITracer) =
    interface ITracer with
        member _.StartTrace(operationName) =
            let span = inner.StartTrace operationName
            Obs.emit bus sessionKey (SpanStarted span)
            span
        member _.StartSpan parentSpan operationName =
            let span = inner.StartSpan parentSpan operationName
            Obs.emit bus sessionKey (SpanStarted span)
            span
        member _.EndSpan span status =
            inner.EndSpan span status
            Obs.emit bus sessionKey (SpanEnded(span, status))
        member _.AddEvent span name attributes =
            inner.AddEvent span name attributes
            Obs.emit bus sessionKey (SpanEventAdded(span, name, attributes))
        member _.SetAttributes span attributes =
            inner.SetAttributes span attributes
            Obs.emit bus sessionKey (SpanAttributesSet(span, attributes))
        member _.GetTrace traceId = inner.GetTrace traceId

/// Tee metrics collector: records to the backing collector (so GetMetrics/EstimateCost
/// aggregations stay correct) and publishes a metric signal.
type private PublishingMetrics(sessionKey: string, bus: IEventBus, inner: IMetricsCollector) =
    interface IMetricsCollector with
        member _.RecordLlmCall inputTokens outputTokens latencyMs =
            inner.RecordLlmCall inputTokens outputTokens latencyMs
            Obs.emit bus sessionKey (LlmCallRecorded(inputTokens, outputTokens, latencyMs))
        member _.RecordToolCall toolName durationMs success =
            inner.RecordToolCall toolName durationMs success
            Obs.emit bus sessionKey (ToolCallRecorded(toolName, durationMs, success))
        member _.RecordMetric point =
            inner.RecordMetric point
            Obs.emit bus sessionKey (MetricRecorded point)
        member _.GetMetrics() = inner.GetMetrics()
        member _.EstimateCost costModel = inner.EstimateCost costModel

/// Tee execution journal: persists to the backing journal (so revert reads work) and
/// publishes a record/revert signal after the write completes.
type private PublishingJournal(sessionKey: string, bus: IEventBus, inner: IExecutionJournal) =
    interface IExecutionJournal with
        member _.RecordAsync record =
            task {
                do! inner.RecordAsync record
                do! bus.PublishAsync(ObservabilityCaptured(Obs.buildScope sessionKey, ExecutionRecorded record))
            }
            :> Task
        member _.GetHistoryAsync() = inner.GetHistoryAsync()
        member _.GetRevertibleAsync() = inner.GetRevertibleAsync()
        member _.MarkRevertedAsync record =
            task {
                do! inner.MarkRevertedAsync record
                do! bus.PublishAsync(ObservabilityCaptured(Obs.buildScope sessionKey, ExecutionReverted record))
            }
            :> Task

/// Tee trace store: saves to the backing store (so GetBaselineAsync regression reads work)
/// and publishes a trace-saved signal.
type private PublishingTraceStore(sessionKey: string, bus: IEventBus, inner: ITraceStore) =
    interface ITraceStore with
        member _.SaveAsync trace =
            task {
                do! inner.SaveAsync trace
                do! bus.PublishAsync(ObservabilityCaptured(Obs.buildScope sessionKey, TraceSaved trace))
            }
        member _.GetBaselineAsync agentId taskPattern = inner.GetBaselineAsync agentId taskPattern
        member _.GetTracesAsync agentId limit = inner.GetTracesAsync agentId limit

/// Tee audit log: records to the backing log (so queries work) and publishes an
/// audit-recorded signal.
type private PublishingAuditLog(sessionKey: string, bus: IEventBus, inner: IAuditLog) =
    interface IAuditLog with
        member _.RecordAsync entry =
            task {
                do! inner.RecordAsync entry
                do! bus.PublishAsync(ObservabilityCaptured(Obs.buildScope sessionKey, AuditRecorded entry))
            }
        member _.QueryAsync agentId since = inner.QueryAsync agentId since
        member _.QueryByExecutionAsync executionId = inner.QueryByExecutionAsync executionId
        member _.GetDeniedCountAsync agentId since = inner.GetDeniedCountAsync agentId since

/// An IHarnessServices bundle whose every write is teed to the bus as an ObservabilityCaptured
/// event while reads delegate to the wrapped backing bundle. The grain hands this to the agent
/// harness, so the full observability stream flows through the bus without the producer ever
/// deciding where it is stored.
type PublishingHarnessServices(sessionKey: string, bus: IEventBus, backing: IHarnessServices) =
    let tracer = backing.Tracer |> Option.map (fun t -> PublishingTracer(sessionKey, bus, t) :> ITracer)
    let metrics = backing.Metrics |> Option.map (fun m -> PublishingMetrics(sessionKey, bus, m) :> IMetricsCollector)
    let journal = backing.ExecutionJournal |> Option.map (fun j -> PublishingJournal(sessionKey, bus, j) :> IExecutionJournal)
    let traceStore = backing.TraceStore |> Option.map (fun s -> PublishingTraceStore(sessionKey, bus, s) :> ITraceStore)
    let auditLog = backing.AuditLog |> Option.map (fun a -> PublishingAuditLog(sessionKey, bus, a) :> IAuditLog)

    interface IHarnessServices with
        member _.Tracer = tracer
        member _.Metrics = metrics
        member _.ExecutionJournal = journal
        member _.TraceStore = traceStore
        member _.AuditLog = auditLog

/// Decides WHERE observability data is stored. Returns the harness-services bundle the grain
/// hands to the harness; swapping the strategy moves all observability with zero producer
/// changes (the bundle still publishes the same event stream either way).
type IObservabilityStorageStrategy =
    abstract member ServicesFor: sessionKey: string -> IHarnessServices

/// Session-based strategy: each session's observability lives in its own backing bundle
/// (e.g. sessions/<key>/observability/), built lazily by `backingFactory` and memoised.
type SessionObservabilityStrategy(bus: IEventBus, backingFactory: string -> IHarnessServices) =
    let backings = ConcurrentDictionary<string, IHarnessServices>()
    let backingFor (sessionKey: string) = backings.GetOrAdd(sessionKey, fun k -> backingFactory k)

    interface IObservabilityStorageStrategy with
        member _.ServicesFor(sessionKey) =
            PublishingHarnessServices(sessionKey, bus, backingFor sessionKey) :> IHarnessServices

/// Category-based strategy: every session shares one backing bundle (a single observability
/// folder). Demonstrates that switching the layout needs no producer change.
type CategoryObservabilityStrategy(bus: IEventBus, backing: IHarnessServices) =
    interface IObservabilityStorageStrategy with
        member _.ServicesFor(sessionKey) =
            PublishingHarnessServices(sessionKey, bus, backing) :> IHarnessServices
