namespace Nao.Persistence

open Nao.Agents

/// Selects which backend the persistence factories should produce. This is the single
/// knob a host turns to choose volatile (testing) vs durable (production) storage.
[<RequireQualifiedAccess>]
type PersistenceMode =
    /// Volatile in-memory implementations (ideal for tests and ephemeral runs).
    | InMemory
    /// FileSystem-backed implementations rooted at the given directory.
    | File of baseDir: string
    /// Provider-agnostic ADO.NET implementations using the supplied connection factory.
    | Database of factory: IDbConnectionFactory

/// Builds concrete persistence components for a chosen `PersistenceMode`. The rest of
/// the system depends only on the interfaces; this module is the one place that maps a
/// mode to an implementation, so swapping in-memory for file/database is a single edit.
module Persistence =

    /// Tracer for the given mode.
    let tracer (mode: PersistenceMode) : ITracer =
        match mode with
        | PersistenceMode.InMemory -> InMemoryTracer() :> ITracer
        | PersistenceMode.File dir -> Tracers.file dir
        | PersistenceMode.Database factory -> Tracers.ado factory

    /// Metrics collector for the given mode.
    let metrics (mode: PersistenceMode) : IMetricsCollector =
        match mode with
        | PersistenceMode.InMemory -> InMemoryMetricsCollector() :> IMetricsCollector
        | PersistenceMode.File dir -> MetricsCollectors.file dir
        | PersistenceMode.Database factory -> MetricsCollectors.ado factory

    /// Execution journal for the given mode.
    let executionJournal (mode: PersistenceMode) : IExecutionJournal =
        match mode with
        | PersistenceMode.InMemory -> InMemoryExecutionJournal() :> IExecutionJournal
        | PersistenceMode.File dir -> ExecutionJournals.file dir
        | PersistenceMode.Database factory -> ExecutionJournals.ado factory

    /// Trace store (regression baselines) for the given mode.
    let traceStore (mode: PersistenceMode) : ITraceStore =
        match mode with
        | PersistenceMode.InMemory -> InMemoryTraceStore() :> ITraceStore
        | PersistenceMode.File dir -> TraceStores.file dir
        | PersistenceMode.Database factory -> TraceStores.ado factory

    /// Audit log for the given mode.
    let auditLog (mode: PersistenceMode) : IAuditLog =
        match mode with
        | PersistenceMode.InMemory -> InMemoryAuditLog() :> IAuditLog
        | PersistenceMode.File dir -> AuditLogs.file dir
        | PersistenceMode.Database factory -> AuditLogs.ado factory

    /// Build a full `IHarnessServices` bundle for the mode — the value a host injects
    /// into the Orleans silo / ASP.NET container so the `SessionGrain` (and any harness)
    /// gets observability + governance wired to the chosen backend.
    let harnessServices (mode: PersistenceMode) : IHarnessServices =
        HarnessServices.create
            (Some(tracer mode))
            (Some(metrics mode))
            (Some(executionJournal mode))
            (Some(traceStore mode))
            (Some(auditLog mode))
