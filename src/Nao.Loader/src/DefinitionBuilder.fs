namespace Nao.Loader

open System.Diagnostics
open System.Threading.Tasks
open Nao.Agents
open Nao.Core
open Nao.Eval

/// Builds runnable domain objects from parsed definitions
module DefinitionBuilder =

    /// Build a Tool from a ToolDef (command-based execution)
    let buildTool (def: ToolDef) : Tool =
        { Name = def.Name
          Description = def.Description
          Execute = fun input ->
            task {
                let psi = ProcessStartInfo(def.Command)
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                for arg in def.Args do
                    psi.ArgumentList.Add(arg)
                psi.ArgumentList.Add(input)

                use proc = Process.Start(psi)
                let! output = proc.StandardOutput.ReadToEndAsync()
                do! proc.WaitForExitAsync()
                return output.TrimEnd()
            } }

    /// Build an OrchestratorConfig from an AgentDef
    let buildOrchestratorConfig
        (provider: ILlmProvider)
        (tools: Tool list)
        (subAgents: IAgent list)
        (def: AgentDef)
        : OrchestratorConfig =
        { Provider = provider
          Tools = tools
          SubAgents = subAgents
          Prompt = def.Prompt
          Options = def.Options
          MaxRounds = def.MaxRounds
          EventSink = AgentEventSink.none
          Memory = OrchestratorMemoryConfig.None }

    /// Build an IAgent from an AgentDef
    let buildAgent
        (provider: ILlmProvider)
        (tools: Tool list)
        (subAgents: IAgent list)
        (def: AgentDef)
        : IAgent =
        let config = buildOrchestratorConfig provider tools subAgents def
        Orchestrator.createWithConfig config

    /// Build an EvalDataset from an EvalSuiteDef
    let buildEvalDataset (def: EvalSuiteDef) : EvalDataset =
        { Name = def.Name
          Cases = def.Cases }
