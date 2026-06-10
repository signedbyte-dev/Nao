namespace Nao.Agents

open System.Threading.Tasks

/// The result of a single harness step
type HarnessStepResult =
    { Action: AgentAction
      ElapsedMs: int64
      Logs: LogEntry list }

/// Overall result of a harness run
type HarnessRunResult =
    { FinalResponse: string
      Steps: HarnessStepResult list
      TotalElapsedMs: int64
      TotalLogs: LogEntry list }

/// Unified error type for the ETCLOVG harness, covering all failure modes
[<RequireQualifiedAccess>]
type HarnessError =
    /// Agent execution permissions denied
    | PermissionDenied
    /// Policy engine blocked execution
    | PolicyBlocked of violations: string list
    /// Readiness checks failed (prerequisites not met)
    | NotReady of reasons: string list
    /// Lifecycle initialization failed
    | InitializationFailed of message: string
    /// Resource limit exceeded during execution
    | ResourceLimitExceeded of limit: LimitExceeded
    /// Agent output violates constitution rules
    | ConstitutionViolation of ruleIds: string list
    /// Unexpected error during execution
    | ExecutionFailed of message: string

    /// Get a human-readable error message
    member this.Message =
        match this with
        | PermissionDenied -> "Permission denied"
        | PolicyBlocked violations -> sprintf "Blocked by policy: %s" (violations |> String.concat "; ")
        | NotReady reasons -> sprintf "Not ready: %s" (reasons |> String.concat "; ")
        | InitializationFailed msg -> msg
        | ResourceLimitExceeded limit -> sprintf "Resource limit exceeded: %A" limit
        | ConstitutionViolation ruleIds -> sprintf "Output violates constitution: %s" (ruleIds |> String.concat ", ")
        | ExecutionFailed msg -> msg
