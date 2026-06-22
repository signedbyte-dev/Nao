namespace Nao.Runtime.Orleans.Grains

open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Orleans

/// Built-in executor for "agent" tasks: drives a dedicated sub-session conversation.
///
/// The sub-session is a real `ISessionGrain` (key = the task's "userId/sessionId/taskId"),
/// so it gets the full harness, transcript, and history machinery. It inherits the parent
/// session's workspace, tools, and runtime, and is marked `Kind = "task"` so it runs the
/// (otherwise async) agent inline instead of recursively spawning another task.
type AgentTaskExecutor() =

    interface ITaskExecutor with
        member _.Kind = "agent"

        member _.ExecuteAsync(ctx: TaskExecutionContext) : Task<TaskOutcome> =
            task {
                let p =
                    match JsonSerializer.Deserialize<Dictionary<string, string>>(ctx.ParamsJson) with
                    | null -> Dictionary<string, string>()
                    | d -> d
                let agentName = match p.TryGetValue "agent" with | true, v -> v | _ -> ""
                let input = match p.TryGetValue "input" with | true, v -> v | _ -> ""

                let parent = ctx.GrainFactory.GetGrain<ISessionGrain>(ctx.ParentKey)
                let! parentInfo = parent.GetInfoAsync()

                let sub = ctx.GrainFactory.GetGrain<ISessionGrain>(ctx.SubSessionKey)
                let opts = SessionStartOptions()
                opts.AgentName <- agentName
                opts.WorkspaceKey <- parentInfo.WorkspaceKey
                opts.ToolNames <- ResizeArray(parentInfo.ToolNames)
                opts.RuntimeMode <- parentInfo.RuntimeMode
                opts.Kind <- "task"
                opts.ParentKey <- ctx.ParentKey

                ctx.Report 0.1 (sprintf "Starting %s" agentName)
                let! started = sub.StartAsync(opts)
                if not started then
                    return { Summary = sprintf "[Error] Could not start sub-session for agent '%s'" agentName
                             ResultFileIds = [] }
                else
                    ctx.Report 0.4 "Working"
                    let! answer = sub.ProcessAsync(input)
                    ctx.Report 1.0 "Completed"
                    return { Summary = answer; ResultFileIds = [] }
            }
