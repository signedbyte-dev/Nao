namespace Nao.Agents

open System
open System.Diagnostics
open System.Threading.Tasks
open Nao.Core

/// Complete ETCLOVG harness configuration wiring all seven layers together
type EtclovgConfig =
    { /// E — Execution Environment: sandbox and resource limits
      Execution: SandboxConfig
      /// T — Tool Interface: protocol for tool discovery and invocation
      ToolProtocol: IToolProtocol option
      /// C — Context & Memory: tiered memory configuration
      MemoryConfig: OrchestratorMemoryConfig
      /// L — Lifecycle: hooks and pipeline stages
      Lifecycle: ILifecycleHook list
      /// O — Observability: tracer, metrics, and resilience
      Tracer: ITracer option
      Metrics: IMetricsCollector option
      Resilience: ResilienceConfig
      /// V — Verification: readiness checks, trace store, and judge
      ReadinessChecks: IReadinessCheck list
      TraceStore: ITraceStore option
      Judge: IJudge option
      /// G — Governance: permissions, constitution, audit, policies
      Permissions: PermissionModel option
      Constitution: Constitution option
      AuditLog: IAuditLog option
      PolicyEngine: PolicyEngine option
      /// Event sink for all events
      EventSink: IAgentEventSink }

    static member Default =
        { Execution = SandboxConfig.Default
          ToolProtocol = None
          MemoryConfig = OrchestratorMemoryConfig.None
          Lifecycle = []
          Tracer = None
          Metrics = None
          Resilience = ResilienceConfig.NoResilience
          ReadinessChecks = []
          TraceStore = None
          Judge = None
          Permissions = None
          Constitution = None
          AuditLog = None
          PolicyEngine = None
          EventSink = AgentEventSink.none }

    static member WithObservability (tracer: ITracer) (metrics: IMetricsCollector) =
        { EtclovgConfig.Default with Tracer = Some tracer; Metrics = Some metrics }

/// Result of an ETCLOVG harness execution
type EtclovgResult =
    { /// The agent's final response (if successful)
      Response: string option
      /// Whether execution succeeded
      Success: bool
      /// Error if execution failed (legacy string for backwards compat)
      Error: string option
      /// Structured error type (preferred over Error string)
      HarnessError: HarnessError option
      /// Resource usage
      Usage: ResourceUsage
      /// Execution trace
      Trace: ExecutionTrace option
      /// Metrics collected during execution
      Metrics: ExecutionMetrics option
      /// Judgement result (if judge configured)
      Judgement: JudgementResult option
      /// Regression result (if baseline available)
      Regression: RegressionResult option
      /// Audit entries generated
      AuditEntries: int
      /// Policy violations
      PolicyViolations: PolicyViolation list
      /// Constitution violations
      ConstitutionViolations: ConstitutionViolation list }

/// The ETCLOVG Harness — integrates all seven layers into a unified execution pipeline
module EtclovgHarness =

    let private failResult (harnessError: HarnessError) (usage: ResourceUsage) (trace: ExecutionTrace) (policyViolations: PolicyViolation list) (constitutionViolations: ConstitutionViolation list) (metrics: IMetricsCollector option) (auditEntries: int) : EtclovgResult =
        { Response = None; Success = false; Error = Some harnessError.Message
          HarnessError = Some harnessError
          Usage = usage; Trace = Some trace
          Metrics = metrics |> Option.map (fun m -> m.GetMetrics())
          Judgement = None; Regression = None; AuditEntries = auditEntries
          PolicyViolations = policyViolations; ConstitutionViolations = constitutionViolations }

    /// Run an agent through the full ETCLOVG harness
    let runAsync (config: EtclovgConfig) (agent: IAgent) (input: string) : Task<EtclovgResult> =
        task {
            let execCtx = ExecutionContext.Create config.Execution
            let emit = config.EventSink.Emit
            let mutable trace = Verification.startTrace agent.Id input
            let mutable policyViolations = []
            let mutable constitutionViolations = []

            // === G: Governance — Check permissions ===
            let permissionDenied =
                match config.Permissions with
                | Some perms ->
                    match PermissionModel.check perms "execute" with
                    | PermissionLevel.Deny -> true
                    | _ -> false
                | None -> false

            if permissionDenied then
                return failResult HarnessError.PermissionDenied execCtx.Usage trace [] [] None 0
            else

            // === G: Policy engine pre-check ===
            let policyBlocked =
                match config.PolicyEngine with
                | Some engine ->
                    let ctx = PolicyContext.FromExecutionContext agent.Id "execute" (Some input) execCtx
                    let result = engine.Evaluate(ctx)
                    policyViolations <- result.Violations
                    if not result.Proceed then
                        Some (result.Violations |> List.map (fun v -> v.Message))
                    else None
                | None -> None

            match policyBlocked with
            | Some violations ->
                return failResult (HarnessError.PolicyBlocked violations) execCtx.Usage trace policyViolations [] None 0
            | None ->

            // === V: Verification — Readiness checks ===
            let! readiness =
                if config.ReadinessChecks.Length > 0 then
                    Verification.checkReadiness config.ReadinessChecks agent.Id input
                else
                    Task.FromResult ReadinessResult.Ready

            match readiness with
            | ReadinessResult.NotReady reasons ->
                emit (AgentEvent.Log (LogLevel.Warning, "harness", sprintf "Readiness check failed: %s" (reasons |> String.concat "; "), Map.empty))
                return failResult (HarnessError.NotReady reasons) execCtx.Usage trace policyViolations [] None 0
            | ReadinessResult.Ready ->

            // === L: Lifecycle — Initialize ===
            let lifecycle = AgentLifecycle.create () |> AgentLifecycle.withHooks config.Lifecycle
            let! initResult = AgentLifecycle.initializeAsync agent.Id lifecycle

            match initResult with
            | Error msg ->
                return failResult (HarnessError.InitializationFailed msg) execCtx.Usage trace policyViolations [] None 0
            | Ok initializedLc ->

            // === L: Lifecycle — Start ===
            let! _startedLc = AgentLifecycle.startAsync agent.Id input initializedLc

            // === O: Observability — Start trace span ===
            let rootSpan =
                config.Tracer
                |> Option.map (fun t ->
                    let s = t.StartTrace(sprintf "harness:%s" agent.Id.Name)
                    t.SetAttributes s (Map.ofList ["agent.name", agent.Id.Name; "input", input; "execution.id", string execCtx.ExecutionId])
                    s)

            // === T: Tool Protocol — Record available tools in span ===
            match config.ToolProtocol, rootSpan, config.Tracer with
            | Some protocol, Some span, Some tracer ->
                let! tools = protocol.ListTools()
                let toolNames = tools |> List.map (fun t -> t.Name) |> String.concat ","
                tracer.SetAttributes span (Map.ofList ["tools.available", toolNames; "tools.count", string tools.Length])
            | _ -> ()

            // === E: Execution — Run agent within sandbox ===
            let sw = Stopwatch.StartNew()
            let execSpan =
                match rootSpan, config.Tracer with
                | Some parent, Some tracer ->
                    let s = tracer.StartSpan parent "agent.execute"
                    tracer.SetAttributes s (Map.ofList ["sandbox.isolation", string config.Execution.Isolation])
                    Some s
                | _ -> None
            let env = ExecutionEnvironment.local ()
            let! execResult = env.ExecuteAsync execCtx agent input
            sw.Stop()

            // === O: Record metrics ===
            config.Metrics |> Option.iter (fun m -> m.RecordLlmCall 0 0 sw.ElapsedMilliseconds)

            // === O: End execution span ===
            match execSpan, config.Tracer with
            | Some s, Some tracer ->
                match execResult with
                | Ok _ -> tracer.EndSpan s SpanStatus.Ok
                | Error e -> tracer.EndSpan s (SpanStatus.Error (sprintf "%A" e))
            | _ -> ()

            match execResult with
            | Error limitExceeded ->
                let! _ = AgentLifecycle.failAsync agent.Id (exn (sprintf "Limit exceeded: %A" limitExceeded)) _startedLc
                trace <- trace |> Verification.fail (sprintf "Limit exceeded: %A" limitExceeded)
                match config.TraceStore with
                | Some store -> do! store.SaveAsync trace
                | None -> ()
                // End root span on failure
                match rootSpan, config.Tracer with
                | Some s, Some tracer -> tracer.EndSpan s (SpanStatus.Error (sprintf "Limit exceeded: %A" limitExceeded))
                | _ -> ()
                return failResult (HarnessError.ResourceLimitExceeded limitExceeded) execCtx.Usage trace policyViolations [] config.Metrics 0

            | Ok response ->
                // === G: Constitution — Check output ===
                let constitutionBlocked =
                    match config.Constitution with
                    | Some constitution ->
                        let checkResult = Constitution.check constitution response
                        constitutionViolations <- checkResult.Violations
                        Constitution.hasHardViolations checkResult
                    | None -> false

                if constitutionBlocked then
                    // Audit the violation
                    match config.AuditLog with
                    | Some audit ->
                        let violationNames = constitutionViolations |> List.map (fun v -> v.RuleId)
                        do! audit.RecordAsync (AuditLog.constitutionCheck agent.Id violationNames (Some execCtx.ExecutionId))
                    | None -> ()
                    let! _ = AgentLifecycle.failAsync agent.Id (exn "Constitution violation") _startedLc
                    // End root span on constitution violation
                    match rootSpan, config.Tracer with
                    | Some s, Some tracer -> tracer.EndSpan s (SpanStatus.Error "Constitution violation")
                    | _ -> ()
                    let violationIds = constitutionViolations |> List.map (fun v -> v.RuleId)
                    return
                        { Response = None; Success = false
                          Error = Some "Output violates constitution"
                          HarnessError = Some (HarnessError.ConstitutionViolation violationIds)
                          Usage = execCtx.Usage; Trace = Some trace
                          Metrics = config.Metrics |> Option.map (fun m -> m.GetMetrics())
                          Judgement = None; Regression = None; AuditEntries = 1
                          PolicyViolations = policyViolations
                          ConstitutionViolations = constitutionViolations }
                else

                // === L: Lifecycle — Complete ===
                let! _ = AgentLifecycle.completeAsync agent.Id response _startedLc

                // === V: Complete trace and store ===
                trace <- trace |> Verification.addStep (TraceAction.LlmCall "unknown") input response sw.ElapsedMilliseconds
                trace <- trace |> Verification.complete response

                // === V: Judge the execution ===
                let! judgement =
                    match config.Judge with
                    | Some judge ->
                        task {
                            let! j = judge.JudgeAsync trace
                            return Some j
                        }
                    | None -> Task.FromResult None

                // === V: Regression detection ===
                let! regression =
                    match config.TraceStore with
                    | Some store ->
                        task {
                            let! baseline = store.GetBaselineAsync agent.Id input
                            match baseline with
                            | Some b -> return Some (Regression.detect b trace)
                            | None -> return None
                        }
                    | None -> Task.FromResult None

                // === V: Save trace ===
                match config.TraceStore with
                | Some store -> do! store.SaveAsync trace
                | None -> ()

                // === G: Audit ===
                match config.AuditLog with
                | Some audit ->
                    do! audit.RecordAsync (AuditLog.llmCall agent.Id "unknown" (Some execCtx.ExecutionId))
                | None -> ()

                // === O: End root span ===
                match rootSpan, config.Tracer with
                | Some s, Some tracer ->
                    tracer.AddEvent s "harness.complete" (Map.ofList ["response.length", string response.Length])
                    tracer.EndSpan s SpanStatus.Ok
                | _ -> ()

                emit (AgentEvent.Completed response)

                return
                    { Response = Some response
                      Success = true
                      Error = None
                      HarnessError = None
                      Usage = execCtx.Usage
                      Trace = Some trace
                      Metrics = config.Metrics |> Option.map (fun m -> m.GetMetrics())
                      Judgement = judgement
                      Regression = regression
                      AuditEntries = 1
                      PolicyViolations = policyViolations
                      ConstitutionViolations = constitutionViolations }
        }
