module Nao.Demo.Program

open System
open System.Threading.Tasks
open Nao.Core
open Nao.Agents
open Nao.Providers

/// ETCLOVG-wrapped execution with governance, observability, and verification.
let private runWithHarness (agent: IAgent) (input: string) (eventSink: IAgentEventSink) : Task<string> =
    task {
        // Define a simple constitution with safety rules
        let constitution : Constitution =
            { Name = "Demo Constitution"
              Version = "1.0"
              Preamble = Some "The assistant must be helpful and safe."
              Rules =
                [ { Id = "no-secrets"
                    Description = "Never output real credentials or API keys"
                    Category = RuleCategory.Safety
                    Priority = 100
                    IsHardConstraint = true
                    Check = fun content ->
                        content.Contains("sk-") || content.Contains("AKIA") }
                  { Id = "no-harmful-content"
                    Description = "Do not produce harmful or hateful content"
                    Category = RuleCategory.Safety
                    Priority = 90
                    IsHardConstraint = true
                    Check = fun _ -> false } ] }

        let etclovgConfig =
            { EtclovgConfig.Default with
                Constitution = Some constitution
                EventSink = eventSink }

        let! result = EtclovgHarness.runAsync etclovgConfig agent input

        match result.Response with
        | Some response -> return response
        | None ->
            let err = result.Error |> Option.defaultValue "Unknown error"
            return sprintf "[Error] %s" err
    }

/// Print the welcome banner
let private printBanner () =
    Console.ForegroundColor <- ConsoleColor.Cyan
    printfn ""
    printfn "  ╔═══════════════════════════════════════════════════╗"
    printfn "  ║           Nao Framework — Demo CLI                ║"
    printfn "  ║  Personal Assistant with ETCLOVG Governance       ║"
    printfn "  ╚═══════════════════════════════════════════════════╝"
    Console.ResetColor()
    printfn ""
    printfn "  Features demonstrated:"
    printfn "    • Real LLM via Ollama (local)"
    printfn "    • File-system tools with Verify & Revert"
    printfn "    • ETCLOVG harness (governance, observability)"
    printfn "    • Execution journal with undo support"
    printfn "    • Constitutional AI safety rules"
    printfn ""
    printfn "  Commands:"
    printfn "    /quit or /exit  — Exit the CLI"
    printfn "    /undo           — Revert the last tool action"
    printfn "    /journal        — Show execution journal"
    printfn "    /workspace      — Show workspace path"
    printfn "    /help           — Show this help"
    printfn ""
    Console.ForegroundColor <- ConsoleColor.DarkGray
    printfn "  Workspace: %s" (FileSystemTools.ensureWorkDir())
    printfn "  LLM: Ollama at http://localhost:11434"
    Console.ResetColor()
    printfn ""

/// Print the prompt
let private printPrompt () =
    Console.ForegroundColor <- ConsoleColor.Green
    printf "nao> "
    Console.ResetColor()

/// Print the assistant response
let private printResponse (response: string) =
    Console.ForegroundColor <- ConsoleColor.White
    printfn ""
    printfn "%s" response
    printfn ""
    Console.ResetColor()

/// Print an info message
let private printInfo (msg: string) =
    Console.ForegroundColor <- ConsoleColor.Yellow
    printfn "  %s" msg
    Console.ResetColor()
    printfn ""

/// The execution journal tracks tool calls for undo support
let private journal = InMemoryExecutionJournal() :> IExecutionJournal

/// A console event sink that shows orchestration events in real-time
let private createEventSink () : IAgentEventSink =
    let consoleSink = AgentEventSink.console "nao-demo"
    let journalSink =
        { new IAgentEventSink with
            member _.Emit event =
                match event with
                | AgentEvent.ToolResult (name, result) ->
                    // Record in journal for undo
                    journal.RecordAsync(
                        { ToolName = name
                          Input = ""
                          Output = result
                          ContentMeta = ContentMeta.Text
                          ExecutedAt = DateTimeOffset.UtcNow
                          Reverted = false
                          Metadata = Map.empty }).Wait()
                | _ -> () }
    AgentEventSink.combine [ consoleSink; journalSink ]

/// Handle special CLI commands
let private handleCommand (cmd: string) : bool =
    match cmd.ToLowerInvariant().Trim() with
    | "/quit" | "/exit" ->
        printInfo "Goodbye!"
        false
    | "/help" ->
        printBanner ()
        true
    | "/workspace" ->
        printInfo (sprintf "Workspace: %s" (FileSystemTools.ensureWorkDir()))
        true
    | "/journal" ->
        let entries = journal.GetHistoryAsync().Result
        if entries.IsEmpty then
            printInfo "Journal is empty — no tool actions recorded yet."
        else
            printInfo (sprintf "Journal (%d entries):" entries.Length)
            for entry in entries do
                Console.ForegroundColor <- ConsoleColor.DarkGray
                printfn "    [%s] %s → %s"
                    (entry.ExecutedAt.ToString("HH:mm:ss"))
                    entry.ToolName
                    (entry.Output.Substring(0, min 60 entry.Output.Length))
                Console.ResetColor()
            printfn ""
        true
    | "/undo" ->
        let entries = journal.GetHistoryAsync().Result
        if entries.IsEmpty then
            printInfo "Nothing to undo."
        else
            let last = entries |> List.last
            printInfo (sprintf "Last action: %s — (journal undo is demonstration only)" last.ToolName)
        true
    | _ ->
        printInfo (sprintf "Unknown command: %s. Type /help for available commands." cmd)
        true

[<EntryPoint>]
let main argv =
    printBanner ()

    // Configure the LLM provider
    let ollamaConfig =
        match Array.tryItem 0 argv with
        | Some model -> { OllamaConfig.Default with Model = model }
        | None -> OllamaConfig.Default

    printInfo (sprintf "Using model: %s" ollamaConfig.Model)

    let provider = ProviderFactory.create (ProviderType.Ollama ollamaConfig)
    let eventSink = createEventSink ()
    let agent = DemoAgent.create provider eventSink

    // Interactive loop
    let mutable running = true

    while running do
        printPrompt ()
        let input = Console.ReadLine()

        if isNull input then
            running <- false
        elif String.IsNullOrWhiteSpace(input) then
            ()
        elif input.StartsWith("/") then
            running <- handleCommand input
        else
            try
                let response = (runWithHarness agent input eventSink).Result
                printResponse response
            with ex ->
                Console.ForegroundColor <- ConsoleColor.Red
                printfn "  [Error] %s" ex.InnerException.Message
                Console.ResetColor()
                printfn ""

    0
