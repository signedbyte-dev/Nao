namespace Nao.Agents

open System.Threading.Tasks

/// Configuration for the agent harness
type HarnessConfig =
    { MaxSteps: int
      TimeoutMs: int option
      Logger: IAgentLogger }

    static member Default =
        { MaxSteps = 20
          TimeoutMs = None
          Logger = AgentLogger.silent }
