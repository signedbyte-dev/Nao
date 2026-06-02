namespace Nao.Runtime.Orleans.Grains

open Orleans
open System.Threading.Tasks
open Nao.Agents

/// Base grain implementation for agent actors.
/// F# note: Orleans grains must be classes with parameterless constructors.
/// Use [<AbstractClass>] for base grains that subclasses extend.
[<AbstractClass>]
type AgentGrainBase() =
    inherit Grain()

    abstract member Agent: IAgent

    interface IAgentGrain with
        member this.ProcessAsync(input: string) : Task<string> =
            this.Agent.RunAsync input

        member this.ReceiveMessageAsync (fromAgent: string) (message: string) : Task<string option> =
            task {
                let senderId = { Name = fromAgent; Description = "" }
                let msg = AgentMessage.broadcast senderId message
                let! reply = this.Agent.HandleMessageAsync msg
                return reply |> Option.map (fun m -> m.Content)
            }

        member this.GetAgentIdAsync() : Task<string> =
            Task.FromResult(this.Agent.Id.Name)
