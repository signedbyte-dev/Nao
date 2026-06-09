namespace Nao.Agents

open System
open System.Text.Json
open System.Threading.Tasks
open Nao.Core

/// Memory management configuration for the orchestrator
type OrchestratorMemoryConfig =
    { /// Strategy for trimming conversation history before each LLM call
      WindowStrategy: WindowStrategy option
      /// Optional summarization config (uses LLM to condense old messages)
      Summarization: SummarizationConfig option
      /// Optional key-value memory store for cross-session facts
      MemoryStore: IMemoryStore option
      /// How many memories to inject into the system prompt context
      MaxMemoriesToInject: int }

    static member None =
        { WindowStrategy = Option.None
          Summarization = Option.None
          MemoryStore = Option.None
          MaxMemoriesToInject = 5 }

    static member WithWindow strategy =
        { OrchestratorMemoryConfig.None with WindowStrategy = Some strategy }

/// Configuration for the Orchestrator
type OrchestratorConfig =
    { /// The LLM provider used for reasoning
      Provider: ILlmProvider
      /// Available tools the orchestrator can invoke
      Tools: Tool list
      /// Available sub-agents the orchestrator can delegate to
      SubAgents: IAgent list
      /// The system prompt for the orchestrator
      Prompt: Prompt
      /// Completion options
      Options: CompletionOptions
      /// Maximum tool/agent invocation rounds before forcing a response
      MaxRounds: int
      /// Event sink for logging, progress, and conversation tracking
      EventSink: IAgentEventSink
      /// Memory management configuration
      Memory: OrchestratorMemoryConfig }

/// The fundamental orchestrator agent.
/// Accepts user input, uses an LLM to decide which tool or sub-agent to invoke,
/// executes the action, feeds results back, and produces a final response.
type Orchestrator(config: OrchestratorConfig) =
    let id = { Name = "orchestrator"; Description = "Routes requests to tools and sub-agents" }
    let mutable state = AgentState.Empty

    let buildSystemPrompt () =
        let toolDescriptions =
            config.Tools
            |> List.map (fun t -> sprintf "  - %s: %s" t.Name t.Description)
            |> String.concat "\n"

        let agentDescriptions =
            config.SubAgents
            |> List.map (fun a -> sprintf "  - %s: %s" a.Id.Name a.Id.Description)
            |> String.concat "\n"

        let basePrompt = Prompt.render config.Prompt

        let capabilities =
            [ if config.Tools.Length > 0 then
                yield sprintf "# Available Tools\n%s" toolDescriptions
              if config.SubAgents.Length > 0 then
                yield sprintf "# Available Agents\n%s" agentDescriptions ]
            |> String.concat "\n\n"

        let instructions = """
# Action Format
When you need to use a tool, respond with EXACTLY this JSON format on a single line:
{"action":"tool","name":"<tool_name>","input":"<input_string>"}

When you need to delegate to a sub-agent, respond with EXACTLY this JSON format:
{"action":"delegate","name":"<agent_name>","input":"<input_string>"}

When you have enough information to answer the user directly, just respond normally with your answer.
Do NOT wrap your final answer in the action JSON format above. Only use the action JSON when invoking a tool or delegating.
If the user requests output in a specific format (e.g. JSON, XML, CSV, Markdown, YAML), encode your final answer in that format."""

        sprintf "%s\n\n%s\n%s" basePrompt capabilities instructions

    let tryParseAction (content: string) : AgentAction option =
        let trimmed = content.Trim()
        if trimmed.StartsWith("{\"action\"") then
            try
                use doc = JsonDocument.Parse(trimmed)
                let root = doc.RootElement

                let getValue (key: string) =
                    match root.TryGetProperty(key) with
                    | true, elem when elem.ValueKind = JsonValueKind.String -> Some (elem.GetString())
                    | _ -> None

                match getValue "action" with
                | Some "tool" ->
                    match getValue "name", getValue "input" with
                    | Some name, Some input -> Some (InvokeTool (name, input))
                    | Some name, None -> Some (InvokeTool (name, ""))
                    | _ -> None
                | Some "delegate" ->
                    match getValue "name", getValue "input" with
                    | Some name, Some input -> Some (DelegateToAgent (name, input))
                    | Some name, None -> Some (DelegateToAgent (name, ""))
                    | _ -> None
                | _ -> None
            with _ -> None
        else
            None

    let findTool name =
        config.Tools |> List.tryFind (fun t -> t.Name = name)

    let findAgent name =
        config.SubAgents |> List.tryFind (fun a -> a.Id.Name = name)

    let emit event = config.EventSink.Emit event

    let applyWindowAsync (conversation: Conversation) : Task<Conversation> =
        task {
            // First try summarization (which uses the LLM)
            let! afterSummary =
                match config.Memory.Summarization with
                | Some summarizationConfig -> Summarizer.applyAsync summarizationConfig conversation
                | Option.None -> Task.FromResult conversation

            // Then apply windowing strategy
            return
                match config.Memory.WindowStrategy with
                | Some strategy -> ConversationWindow.apply strategy afterSummary
                | Option.None -> afterSummary
        }

    let getMemoryContext () : Task<string> =
        task {
            match config.Memory.MemoryStore with
            | Some store ->
                let! memories = store.RecallAllAsync id
                if memories.IsEmpty then return ""
                else
                    let relevant =
                        memories
                        |> List.sortByDescending (fun m -> m.Timestamp)
                        |> List.truncate config.Memory.MaxMemoriesToInject
                        |> List.map (fun m -> sprintf "  - [%s]: %s" m.Key m.Value)
                        |> String.concat "\n"
                    return sprintf "\n\n# Agent Memories\n%s" relevant
            | Option.None -> return ""
        }

    member private _.RunCore(input: string) : Task<string> =
        task {
            let! memoryContext = getMemoryContext ()
            let systemContent = buildSystemPrompt () + memoryContext
            let systemMsg = { Role = System; Content = systemContent }
            let userMsg = { Role = User; Content = input }
            emit (AgentEvent.MessageAdded (User, input))

            // Apply windowing to prior conversation before appending new messages
            let! windowedHistory = applyWindowAsync state.Conversation
            let mutable conversation = windowedHistory @ [ systemMsg; userMsg ]
            let mutable rounds = 0
            let mutable finalAnswer = ""
            let mutable finished = false

            while not finished && rounds < config.MaxRounds do
                emit (AgentEvent.Thinking (rounds + 1))
                let! result = config.Provider.CompleteAsync conversation config.Options
                let assistantMsg = { Role = Assistant; Content = result.Content }
                conversation <- conversation @ [ assistantMsg ]
                emit (AgentEvent.MessageAdded (Assistant, result.Content))

                match tryParseAction result.Content with
                | Some (InvokeTool (toolName, toolInput)) ->
                    emit (AgentEvent.InvokingTool (toolName, toolInput))
                    match findTool toolName with
                    | Some tool ->
                        let! toolResult = tool.Execute toolInput
                        emit (AgentEvent.ToolResult (toolName, toolResult))
                        let resultMsg = { Role = User; Content = sprintf "[Tool Result from %s]: %s" toolName toolResult }
                        conversation <- conversation @ [ resultMsg ]
                    | None ->
                        let err = sprintf "Tool '%s' not found. Available tools: %s" toolName (config.Tools |> List.map (fun t -> t.Name) |> String.concat ", ")
                        emit (AgentEvent.RoundError err)
                        let errMsg = { Role = User; Content = sprintf "[Error]: %s" err }
                        conversation <- conversation @ [ errMsg ]

                | Some (DelegateToAgent (agentName, agentInput)) ->
                    emit (AgentEvent.DelegatingToAgent (agentName, agentInput))
                    match findAgent agentName with
                    | Some agent ->
                        let! agentResult = agent.RunAsync agentInput
                        emit (AgentEvent.AgentResult (agentName, agentResult))
                        let resultMsg = { Role = User; Content = sprintf "[Agent Result from %s]: %s" agentName agentResult }
                        conversation <- conversation @ [ resultMsg ]
                    | None ->
                        let err = sprintf "Agent '%s' not found. Available agents: %s" agentName (config.SubAgents |> List.map (fun a -> a.Id.Name) |> String.concat ", ")
                        emit (AgentEvent.RoundError err)
                        let errMsg = { Role = User; Content = sprintf "[Error]: %s" err }
                        conversation <- conversation @ [ errMsg ]

                | Some (DelegateToAgent _) | Some (Think _) | Some (Respond _) | None ->
                    // No action parsed — this is the final answer
                    finalAnswer <- result.Content
                    finished <- true

                rounds <- rounds + 1

            // If we exhausted rounds without a final answer, do one more call
            if not finished then
                emit (AgentEvent.MaxRoundsReached config.MaxRounds)
                let forceMsg = { Role = User; Content = "[System]: Maximum rounds reached. Please provide your final answer now." }
                conversation <- conversation @ [ forceMsg ]
                let! result = config.Provider.CompleteAsync conversation config.Options
                finalAnswer <- result.Content
                conversation <- conversation @ [ { Role = Assistant; Content = result.Content } ]

            emit (AgentEvent.Completed finalAnswer)
            // Store the full conversation (without system prompt) for future windowing
            let historyMessages =
                conversation
                |> List.filter (fun m -> m.Role <> System)
            state <- { state with Conversation = historyMessages }
            return finalAnswer
        }

    interface IAgent with
        member _.Id = id
        member _.State = state
        member this.RunAsync(input: string) = this.RunCore(input)
        member this.HandleMessageAsync(msg: AgentMessage) =
            task {
                let! response = this.RunCore(msg.Content)
                return Some (AgentMessage.create id msg.From response)
            }

module Orchestrator =

    /// Create an orchestrator with a default prompt
    let create (provider: ILlmProvider) (tools: Tool list) (subAgents: IAgent list) =
        let prompt =
            { Prompt.Empty with
                Role = "You are an intelligent orchestrator agent. You accept user requests and decide the best way to fulfill them using available tools and sub-agents."
                Objective = "Analyze the user's request, determine which tool or agent is best suited, invoke it, and provide a clear final answer based on the results."
                Constraints =
                    [ "Use tools when the user needs factual data, calculations, or external lookups."
                      "Delegate to sub-agents when the task requires specialized expertise."
                      "If you can answer directly without tools, do so."
                      "If the user requests a specific output format (JSON, XML, CSV, etc.), return the final answer in that format."
                      "Do not wrap the final answer in action JSON unless invoking a tool or delegating." ] }

        let config =
            { Provider = provider
              Tools = tools
              SubAgents = subAgents
              Prompt = prompt
              Options = { CompletionOptions.Default with Temperature = 0.1 }
              MaxRounds = 5
              EventSink = AgentEventSink.none
              Memory = OrchestratorMemoryConfig.None }

        Orchestrator(config) :> IAgent

    /// Create an orchestrator with a custom configuration
    let createWithConfig (config: OrchestratorConfig) =
        Orchestrator(config) :> IAgent
