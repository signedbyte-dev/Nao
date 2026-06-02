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
