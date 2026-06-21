namespace Nao.Feedback

open System
open System.Collections.Generic
open Nao.Core
open Nao.Agents

/// An `IAgentEventSink` that accumulates the events of a single turn into a
/// structured `TurnRecord`. Wire it into an `OrchestratorConfig`/`EtclovgConfig`
/// `EventSink` for the duration of one `ProcessAsync` call, then call `Snapshot`.
///
/// The orchestrator emits `InvokingTool`/`ToolResult` (and
/// `DelegatingToAgent`/`AgentResult`) pairs sequentially, so we match each result
/// to the earliest still-unmatched invocation of the same name (FIFO).
type TurnRecorder(turnId: string, sessionId: string, userId: string,
                  workspaceKey: string, agentName: string, agentVersion: string option,
                  input: string,
                  // Resolves a tool name to its known version/provenance for richer records.
                  resolveTool: string -> (string option * ToolProvenance option) option) =

    let sync = obj ()
    let toolCalls = ResizeArray<ToolCallRecord>()
    let subAgentCalls = ResizeArray<SubAgentCallRecord>()
    let steps = ResizeArray<TurnStep>()
    let pendingTools = Dictionary<string, Queue<string>>()
    let pendingAgents = Dictionary<string, Queue<string>>()
    let mutable output = ""

    let enqueue (table: Dictionary<string, Queue<string>>) (key: string) (value: string) =
        match table.TryGetValue key with
        | true, q -> q.Enqueue value
        | _ ->
            let q = Queue<string>()
            q.Enqueue value
            table.[key] <- q

    let dequeue (table: Dictionary<string, Queue<string>>) (key: string) : string option =
        match table.TryGetValue key with
        | true, q when q.Count > 0 -> Some (q.Dequeue())
        | _ -> None

    member _.TurnId = turnId

    /// The ordered process steps — the orchestrator's reasoning per round plus each
    /// tool/sub-agent call — as they happened. Lets a frontend show the whole process,
    /// not just the final answer. The final round's reasoning (which IS the answer) is
    /// omitted since the answer is shown separately.
    member _.Steps : TurnStep list =
        lock sync (fun () ->
            steps
            |> Seq.filter (fun s ->
                not (s.Kind = "reasoning" && s.Output.Trim() = output.Trim()))
            |> List.ofSeq)

    /// The accumulated record so far. Safe to call after the turn completes.
    member _.Snapshot() : TurnRecord =
        lock sync (fun () ->
            { TurnId = turnId
              SessionId = sessionId
              UserId = userId
              WorkspaceKey = workspaceKey
              AgentName = agentName
              AgentVersion = agentVersion
              Input = input
              Output = output
              ToolCalls = List.ofSeq toolCalls
              SubAgentCalls = List.ofSeq subAgentCalls
              CreatedAt = DateTimeOffset.UtcNow })

    interface IAgentEventSink with
        member _.Emit(event: AgentEvent) =
            lock sync (fun () ->
                match event with
                | AgentEvent.MessageAdded (Assistant, content) when not (String.IsNullOrWhiteSpace content) ->
                    // Each round's assistant output: the orchestrator's reasoning / decision.
                    steps.Add { Kind = "reasoning"; Title = "Reasoning"; Input = ""; Output = content }
                | AgentEvent.InvokingTool (name, input) ->
                    enqueue pendingTools name input
                | AgentEvent.ToolResult (name, result) ->
                    let toolInput = dequeue pendingTools name |> Option.defaultValue ""
                    let version, provenance =
                        match resolveTool name with
                        | Some (v, p) -> v, p
                        | None -> None, None
                    toolCalls.Add
                        { Name = name
                          Version = version
                          Input = toolInput
                          Output = result
                          Provenance = provenance }
                    steps.Add { Kind = "tool"; Title = name; Input = toolInput; Output = result }
                | AgentEvent.DelegatingToAgent (name, input) ->
                    enqueue pendingAgents name input
                | AgentEvent.AgentResult (name, result) ->
                    let agentInput = dequeue pendingAgents name |> Option.defaultValue ""
                    subAgentCalls.Add { Name = name; Input = agentInput; Output = result }
                    steps.Add { Kind = "agent"; Title = name; Input = agentInput; Output = result }
                | AgentEvent.Completed answer ->
                    output <- answer
                | _ -> ())

module TurnRecorder =

    /// Create a recorder that does not resolve versions/provenance.
    let create (turnId, sessionId, userId, workspaceKey, agentName, agentVersion, input) =
        TurnRecorder(turnId, sessionId, userId, workspaceKey, agentName, agentVersion, input, (fun _ -> None))

    /// Create a recorder that resolves tool versions/provenance from a known tool list.
    let forTools (tools: Tool list) (turnId, sessionId, userId, workspaceKey, agentName, agentVersion, input) =
        let resolve (name: string) =
            tools
            |> List.tryFind (fun t -> t.Name = name)
            |> Option.map (fun t -> t.Version, t.Provenance)
        TurnRecorder(turnId, sessionId, userId, workspaceKey, agentName, agentVersion, input, resolve)
