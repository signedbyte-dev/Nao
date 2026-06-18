namespace Nao.Persistence

open System
open Nao.Agents

// ----------------------------------------------------------------------------
// Tracer
// ----------------------------------------------------------------------------

/// Event-sourced ITracer. Span identifiers are generated internally, so this is a
/// self-contained implementation (mirroring InMemoryTracer) that persists each
/// span upsert as a full span snapshot and rebuilds the span table on load.
type PersistentTracer(store: IEventStore) =
    let spans = System.Collections.Concurrent.ConcurrentDictionary<SpanId, Span>()

    let upsert (span: Span) =
        spans.[span.Id] <- span
        store.Append(FSharpJson.serialize span)

    do
        for line in store.LoadAll() do
            let span = FSharpJson.deserialize<Span> line
            spans.[span.Id] <- span

    interface ITracer with
        member _.StartTrace(operationName: string) =
            let span =
                { Id = SpanId(Guid.NewGuid())
                  TraceId = TraceId(Guid.NewGuid())
                  ParentSpanId = None
                  OperationName = operationName
                  StartTime = DateTimeOffset.UtcNow
                  EndTime = None
                  Status = SpanStatus.Ok
                  Attributes = Map.empty
                  Events = [] }
            upsert span
            span

        member _.StartSpan (parentSpan: Span) (operationName: string) =
            let span =
                { Id = SpanId(Guid.NewGuid())
                  TraceId = parentSpan.TraceId
                  ParentSpanId = Some parentSpan.Id
                  OperationName = operationName
                  StartTime = DateTimeOffset.UtcNow
                  EndTime = None
                  Status = SpanStatus.Ok
                  Attributes = Map.empty
                  Events = [] }
            upsert span
            span

        member _.EndSpan (span: Span) (status: SpanStatus) =
            upsert { span with EndTime = Some DateTimeOffset.UtcNow; Status = status }

        member _.AddEvent (span: Span) (name: string) (attributes: Map<string, string>) =
            let event = { Name = name; Timestamp = DateTimeOffset.UtcNow; Attributes = attributes }
            upsert { span with Events = span.Events @ [ event ] }

        member _.SetAttributes (span: Span) (attrs: Map<string, string>) =
            upsert { span with Attributes = Map.fold (fun acc k v -> Map.add k v acc) span.Attributes attrs }

        member _.GetTrace(traceId: TraceId) =
            spans.Values |> Seq.filter (fun s -> s.TraceId = traceId) |> Seq.toList

/// Factory helpers for tracer persistence.
module Tracers =
    /// ADO.NET-backed tracer over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : ITracer = PersistentTracer(EventStore.db factory "tracer") :> ITracer

    /// FileSystem-backed tracer rooted at the given directory.
    let file (baseDir: string) : ITracer =
        PersistentTracer(EventStore.file (System.IO.Path.Combine(baseDir, "tracer.jsonl"))) :> ITracer

// ----------------------------------------------------------------------------
// Metrics collector
// ----------------------------------------------------------------------------

/// Mutating events for metrics persistence.
[<RequireQualifiedAccess>]
type MetricsEvent =
    | LlmCall of inputTokens: int * outputTokens: int * latencyMs: int64
    | ToolCall of toolName: string * durationMs: int64 * success: bool
    | Metric of MetricPoint

/// Event-sourced IMetricsCollector.
type PersistentMetricsCollector(store: IEventStore) =
    let inner = InMemoryMetricsCollector() :> IMetricsCollector

    do
        for line in store.LoadAll() do
            match FSharpJson.deserialize<MetricsEvent> line with
            | MetricsEvent.LlmCall(i, o, l) -> inner.RecordLlmCall i o l
            | MetricsEvent.ToolCall(n, d, s) -> inner.RecordToolCall n d s
            | MetricsEvent.Metric p -> inner.RecordMetric p

    interface IMetricsCollector with
        member _.RecordLlmCall (inputTokens: int) (outputTokens: int) (latencyMs: int64) =
            inner.RecordLlmCall inputTokens outputTokens latencyMs
            store.Append(FSharpJson.serialize (MetricsEvent.LlmCall(inputTokens, outputTokens, latencyMs)))

        member _.RecordToolCall (toolName: string) (durationMs: int64) (success: bool) =
            inner.RecordToolCall toolName durationMs success
            store.Append(FSharpJson.serialize (MetricsEvent.ToolCall(toolName, durationMs, success)))

        member _.RecordMetric(point: MetricPoint) =
            inner.RecordMetric point
            store.Append(FSharpJson.serialize (MetricsEvent.Metric point))

        member _.GetMetrics() = inner.GetMetrics()

        member _.EstimateCost(model: CostModel) = inner.EstimateCost model

/// Factory helpers for metrics collector persistence.
module MetricsCollectors =
    /// ADO.NET-backed metrics collector over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : IMetricsCollector =
        PersistentMetricsCollector(EventStore.db factory "metrics") :> IMetricsCollector

    /// FileSystem-backed metrics collector rooted at the given directory.
    let file (baseDir: string) : IMetricsCollector =
        PersistentMetricsCollector(EventStore.file (System.IO.Path.Combine(baseDir, "metrics.jsonl"))) :> IMetricsCollector

// ----------------------------------------------------------------------------
// Trace store (regression baselines)
// ----------------------------------------------------------------------------

/// Mutating events for trace-store persistence.
[<RequireQualifiedAccess>]
type TraceStoreEvent = Save of ExecutionTrace

/// Event-sourced ITraceStore.
type PersistentTraceStore(store: IEventStore) =
    let inner = InMemoryTraceStore() :> ITraceStore

    do
        for line in store.LoadAll() do
            match FSharpJson.deserialize<TraceStoreEvent> line with
            | TraceStoreEvent.Save t -> inner.SaveAsync(t).GetAwaiter().GetResult()

    interface ITraceStore with
        member _.SaveAsync(trace: ExecutionTrace) =
            task {
                do! inner.SaveAsync trace
                store.Append(FSharpJson.serialize (TraceStoreEvent.Save trace))
            }

        member _.GetBaselineAsync (agentId: AgentId) (taskPattern: string) =
            inner.GetBaselineAsync agentId taskPattern

        member _.GetTracesAsync (agentId: AgentId) (limit: int) = inner.GetTracesAsync agentId limit

/// Factory helpers for trace store persistence.
module TraceStores =
    /// ADO.NET-backed trace store over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : ITraceStore =
        PersistentTraceStore(EventStore.db factory "trace-store") :> ITraceStore

    /// FileSystem-backed trace store rooted at the given directory.
    let file (baseDir: string) : ITraceStore =
        PersistentTraceStore(EventStore.file (System.IO.Path.Combine(baseDir, "trace-store.jsonl"))) :> ITraceStore
