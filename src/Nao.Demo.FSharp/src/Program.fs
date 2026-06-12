module Nao.Demo.FSharp.Program

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Hosting
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

let private printBanner () =
    Console.ForegroundColor <- ConsoleColor.Cyan
    printfn ""
    printfn "  ╔═══════════════════════════════════════════════════╗"
    printfn "  ║           Nao Framework — Demo CLI (F#)           ║"
    printfn "  ║  Orleans Grain Session with ETCLOVG Governance    ║"
    printfn "  ╚═══════════════════════════════════════════════════╝"
    Console.ResetColor()
    printfn ""
    printfn "  Features demonstrated:"
    printfn "    • Real LLM via Ollama (local)"
    printfn "    • Orleans grain-based session management"
    printfn "    • Workspace-driven agent/tool resolution"
    printfn "    • ETCLOVG harness (governance, observability)"
    printfn "    • Persistent session state via Orleans"
    printfn ""
    printfn "  Commands:"
    printfn "    /quit or /exit      — Exit the CLI"
    printfn "    /history            — Show conversation history"
    printfn "    /conversations      — List conversation contexts"
    printfn "    /switch <name>      — Switch conversation context"
    printfn "    /clear              — Clear current conversation"
    printfn "    /info               — Show session info"
    printfn "    /help               — Show this help"
    printfn ""
    Console.ForegroundColor <- ConsoleColor.DarkGray
    printfn "  LLM: Ollama at http://localhost:11434"
    Console.ResetColor()
    printfn ""

let private printPrompt () =
    Console.ForegroundColor <- ConsoleColor.Green
    printf "nao> "
    Console.ResetColor()

let private printResponse (response: string) =
    Console.ForegroundColor <- ConsoleColor.White
    printfn ""
    printfn "%s" response
    printfn ""
    Console.ResetColor()

let private printInfo (msg: string) =
    Console.ForegroundColor <- ConsoleColor.Yellow
    printfn "  %s" msg
    Console.ResetColor()
    printfn ""

let private buildHost (ollamaConfig: OllamaConfig) =
    let workspaceRoot =
        Path.Combine(AppContext.BaseDirectory, ".nao")
        |> fun p -> Path.GetFullPath(Path.Combine(p, ".."))

    Host.CreateDefaultBuilder()
        .UseOrleans(fun (siloBuilder: ISiloBuilder) ->
            siloBuilder
                .UseLocalhostClustering()
                .AddMemoryGrainStorage("sessionStore")
            |> ignore)
        .ConfigureServices(fun services ->
            let provider = ProviderFactory.create (ProviderType.Ollama ollamaConfig)
            services.AddSingleton<ILlmProvider>(provider) |> ignore

            let workspace = WorkspaceLoader.loadWorkspace workspaceRoot
            let allTools = FileSystemTools.allTools @ SystemTools.allTools
            let merged =
                { workspace with
                    Tools = workspace.Tools @ allTools }

            let registry = WorkspaceRegistry()
            registry.Register(WorkspaceId.defaultId, merged)
            services.AddSingleton<IWorkspaceRegistry>(registry :> IWorkspaceRegistry) |> ignore)
        .Build()

let private handleCommand (cmd: string) (session: ISessionGrain) : Task<bool> =
    task {
        match cmd.ToLowerInvariant().Trim() with
        | "/quit" | "/exit" ->
            printInfo "Goodbye!"
            return false
        | "/help" ->
            printBanner ()
            return true
        | "/info" ->
            let! info = session.GetInfoAsync()
            printInfo (sprintf "Agent: %s | Workspace: %s | Conversation: %s"
                info.AgentName info.WorkspaceKey info.ActiveConversation)
            printInfo (sprintf "Session: %s/%s | Active: %b"
                info.UserId info.SessionId info.IsActive)
            return true
        | "/history" ->
            let! history = session.GetHistoryAsync()
            if history.IsEmpty then
                printInfo "No conversation history yet."
            else
                for msg in history do
                    let role = match msg.Role with | User -> "You" | Assistant -> "Nao" | _ -> "System"
                    Console.ForegroundColor <- if msg.Role = User then ConsoleColor.Gray else ConsoleColor.White
                    printfn "    [%s] %s" role (msg.Content.Substring(0, min 120 msg.Content.Length))
                Console.ResetColor()
                printfn ""
            return true
        | "/conversations" ->
            let! convs = session.ListConversationsAsync()
            printInfo (sprintf "Conversations: %s" (String.Join(", ", convs)))
            return true
        | "/clear" ->
            do! session.ClearHistoryAsync()
            printInfo "Conversation cleared."
            return true
        | s when s.StartsWith("/switch ") ->
            let name = s.Substring(8).Trim()
            do! session.SwitchConversationAsync(name)
            printInfo (sprintf "Switched to conversation: %s" name)
            return true
        | _ ->
            printInfo (sprintf "Unknown command: %s. Type /help for available commands." cmd)
            return true
    }

[<EntryPoint>]
let main argv =
    printBanner ()

    let ollamaConfig =
        match Array.tryItem 0 argv with
        | Some model -> { OllamaConfig.Default with Model = model }
        | None -> OllamaConfig.Default

    printInfo (sprintf "Using model: %s" ollamaConfig.Model)

    printInfo "Starting Orleans silo..."
    let host = buildHost ollamaConfig
    host.StartAsync().Wait()
    printInfo "Orleans silo started."

    let grainFactory = host.Services.GetRequiredService<IGrainFactory>()
    let userId = Environment.UserName
    let sessionId = Guid.NewGuid().ToString("N").Substring(0, 8)
    let grainKey = sprintf "%s/%s" userId sessionId
    let session = grainFactory.GetGrain<ISessionGrain>(grainKey)

    let startOptions = SessionStartOptions()
    startOptions.AgentName <- "nao-assistant"
    startOptions.WorkspaceKey <- "default"
    startOptions.ToolNames <- ResizeArray(
        [ "create_folder"; "write_file"; "read_file"; "list_folder"; "delete"; "get_datetime"; "calculator" ])

    let started = session.StartAsync(startOptions).Result
    if not started then
        Console.ForegroundColor <- ConsoleColor.Red
        printfn "  [Error] Failed to start session. Check workspace/agent configuration."
        Console.ResetColor()
        host.StopAsync().Wait()
        1
    else
        printInfo (sprintf "Session started: %s" grainKey)

        let mutable running = true

        while running do
            printPrompt ()
            let input = Console.ReadLine()

            if isNull input then
                running <- false
            elif String.IsNullOrWhiteSpace(input) then
                ()
            elif input.StartsWith("/") then
                running <- (handleCommand input session).Result
            else
                try
                    let response = session.ProcessAsync(input).Result
                    printResponse response
                with ex ->
                    let msg =
                        match ex with
                        | :? AggregateException as ae when ae.InnerException <> null -> ae.InnerException.Message
                        | _ -> ex.Message
                    Console.ForegroundColor <- ConsoleColor.Red
                    printfn "  [Error] %s" msg
                    Console.ResetColor()
                    printfn ""

        host.StopAsync().Wait()
        0
