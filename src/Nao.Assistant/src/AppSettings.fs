namespace Nao.Assistant

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

[<CLIMutable>]
type OrchestratorSettings =
    { [<JsonPropertyName("maxRounds")>]
      MaxRounds: int
      [<JsonPropertyName("temperature")>]
      Temperature: float
      [<JsonPropertyName("systemPrompt")>]
      SystemPrompt: string
      [<JsonPropertyName("windowStrategy")>]
      WindowStrategy: string
      [<JsonPropertyName("windowSize")>]
      WindowSize: int
      [<JsonPropertyName("tools")>]
      Tools: string list }

    static member Default =
        { MaxRounds = 10
          Temperature = 0.1
          SystemPrompt = "You are Nao, a helpful assistant."
          WindowStrategy = "LastN"
          WindowSize = 20
          Tools = [] }

[<CLIMutable>]
type ProviderSettings =
    { [<JsonPropertyName("type")>]
      ProviderType: string
      [<JsonPropertyName("endpoint")>]
      Endpoint: string
      [<JsonPropertyName("model")>]
      Model: string }

    static member Default =
        { ProviderType = "Ollama"
          Endpoint = "http://localhost:11434"
          Model = "llama3.2" }

[<CLIMutable>]
type AppSettings =
    { [<JsonPropertyName("provider")>]
      Provider: ProviderSettings
      [<JsonPropertyName("orchestrator")>]
      Orchestrator: OrchestratorSettings
      [<JsonPropertyName("workspacePath")>]
      WorkspacePath: string
      [<JsonPropertyName("theme")>]
      Theme: string }

    static member Default =
        { Provider = ProviderSettings.Default
          Orchestrator = OrchestratorSettings.Default
          WorkspacePath = ""
          Theme = "Dark" }

module AppSettingsStore =

    let private settingsDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nao.Desktop")

    let private settingsPath = Path.Combine(settingsDir, "settings.json")

    let private jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        opts

    let load () : AppSettings =
        if File.Exists(settingsPath) then
            try
                let json = File.ReadAllText(settingsPath)
                JsonSerializer.Deserialize<AppSettings>(json, jsonOptions)
            with _ ->
                AppSettings.Default
        else
            AppSettings.Default

    let save (settings: AppSettings) =
        Directory.CreateDirectory(settingsDir) |> ignore
        let json = JsonSerializer.Serialize(settings, jsonOptions)
        File.WriteAllText(settingsPath, json)

    /// Load orchestrator overrides from a workspace JSON file
    let loadWorkspaceOrchestrator (workspacePath: string) : OrchestratorSettings option =
        let configPath = Path.Combine(workspacePath, ".nao", "orchestrator.json")
        if File.Exists(configPath) then
            try
                let json = File.ReadAllText(configPath)
                Some (JsonSerializer.Deserialize<OrchestratorSettings>(json, jsonOptions))
            with _ -> None
        else
            None
