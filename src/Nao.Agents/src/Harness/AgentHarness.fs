namespace Nao.Agents

open System.Diagnostics
open System.Threading.Tasks

/// The agent harness orchestrates agent execution with logging and step tracking
module AgentHarness =

    /// Run an agent with full harness instrumentation
    let runAsync (config: HarnessConfig) (agent: IAgent) (input: string) : Task<HarnessRunResult> =
        task {
            let sw = Stopwatch.StartNew()
            let allLogs = ResizeArray<LogEntry>()
            let steps = ResizeArray<HarnessStepResult>()

            config.Logger.Log LogLevel.Info (sprintf "Harness starting with input: %s" input)

            let! response = agent.RunAsync input

            sw.Stop()
            config.Logger.Log LogLevel.Info (sprintf "Harness completed in %dms" sw.ElapsedMilliseconds)

            return
                { FinalResponse = response
                  Steps = steps |> Seq.toList
                  TotalElapsedMs = sw.ElapsedMilliseconds
                  TotalLogs = allLogs |> Seq.toList }
        }
