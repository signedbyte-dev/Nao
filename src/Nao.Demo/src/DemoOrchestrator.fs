namespace Nao.Demo

open System.Threading
open System.Threading.Tasks
open Nao.Agents

/// Event raised when the orchestrator wants user confirmation before executing a tool action.
type ToolConfirmationRequest =
    { ToolName: string
      Input: string
      Completion: TaskCompletionSource<bool> }

/// Custom orchestrator that prompts the user for confirmation before tool execution.
type DemoOrchestrator(config: OrchestratorConfig, onConfirmation: ToolConfirmationRequest -> unit) =
    inherit OrchestratorBase(config)

    override this.TryParseActionAsync(content: string) =
        task {
            match this.DefaultTryParseAction(content) with
            | Some (InvokeTool (toolName, input)) ->
                let tcs = TaskCompletionSource<bool>()
                let request = { ToolName = toolName; Input = input; Completion = tcs }
                onConfirmation request
                let! confirmed = tcs.Task
                if confirmed then
                    return Some (InvokeTool (toolName, input))
                else
                    return None
            | other -> return other
        }

/// Factory that creates DemoOrchestrator instances with user confirmation support.
type DemoOrchestratorFactory(onConfirmation: ToolConfirmationRequest -> unit) =
    interface IOrchestratorFactory with
        member _.Create(config) = DemoOrchestrator(config, onConfirmation) :> IAgent
