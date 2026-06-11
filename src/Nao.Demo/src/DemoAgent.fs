namespace Nao.Demo

open System.Threading.Tasks
open Nao.Core
open Nao.Agents

/// A simple agent definition that wraps the Orchestrator for interactive use.
/// Demonstrates: IAgent interface, OrchestratorConfig, tool wiring, event sink.
module DemoAgent =

    /// Create the orchestrator-based agent with all demo tools wired up.
    let create (provider: ILlmProvider) (eventSink: IAgentEventSink) : IAgent =
        let allTools = FileSystemTools.allTools @ SystemTools.allTools

        let prompt =
            { Prompt.Empty with
                Role = "You are Nao, a helpful personal assistant powered by the Nao agent framework."
                Objective = "Help the user accomplish tasks by using tools when appropriate. You can create files and folders, read and list them, do basic math, and tell the current time."
                DomainKnowledge =
                    [ "The workspace root for file operations is ~/.nao-demo-workspace — all file/folder paths are relative to this directory"
                      "When the user says 'my folder' or 'home folder' in the context of file operations, they mean the workspace root (~/.nao-demo-workspace)"
                      "To list the workspace root, use list_folder with empty input"
                      "When asked to create files, use the write_file tool with format 'path|content'"
                      "When asked about directory contents, use list_folder with a relative path or empty for root"
                      "For math questions, use the calculator tool with format 'a op b' (e.g. '2 + 3')"
                      "For date/time questions, use get_datetime" ]
                Constraints =
                    [ "Never execute destructive operations without the user explicitly asking"
                      "Always explain what you did after performing an action"
                      "Keep responses concise and helpful" ]
                OutputFormat = FreeText }

        let config : OrchestratorConfig =
            { Provider = provider
              Tools = allTools
              SubAgents = []
              Prompt = prompt
              Options = { CompletionOptions.Default with Temperature = 0.3 }
              MaxRounds = 10
              EventSink = eventSink
              Memory = OrchestratorMemoryConfig.None }

        Orchestrator(config) :> IAgent
