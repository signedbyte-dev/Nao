namespace Nao.Loader

open System.IO
open System.Text.Json

/// Loads definitions from a directory of JSON files.
///
/// Expected folder structure:
///   <rootDir>/
///   ├── agents/
///   │   ├── my-agent.json
///   │   └── research-agent.json
///   ├── tools/
///   │   ├── web-search.json
///   │   └── calculator.json
///   └── evals/
///       ├── qa-suite.json
///       └── coding-suite.json
type JsonSource(rootDir: string) =

    let parseFile (filePath: string) (reader: JsonElement -> 'T) : LoadResult<'T> =
        if not (File.Exists filePath) then
            Result.Error (FileNotFound filePath)
        else
            try
                let json = File.ReadAllText(filePath)
                use doc = JsonDocument.Parse(json)
                Result.Ok (reader doc.RootElement)
            with ex ->
                Result.Error (ParseError (filePath, ex.Message))

    let loadFromDir (subDir: string) (reader: JsonElement -> 'T) : LoadResult<'T> list =
        let dir = Path.Combine(rootDir, subDir)
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.json")
            |> Array.map (fun f -> parseFile f reader)
            |> Array.toList
        else []

    interface IDefinitionSource with
        member _.Name = sprintf "json:%s" rootDir

        member _.Load() =
            { Agents = loadFromDir "agents" JsonRead.agentDef
              Tools = loadFromDir "tools" JsonRead.toolDef
              EvalSuites = loadFromDir "evals" JsonRead.evalSuiteDef
              BuiltAgents = []
              BuiltTools = []
              BuiltEvaluators = [] }

module JsonSource =

    /// Create a JSON source from a .nao/ directory within a workspace root
    let fromWorkspace (workspaceRoot: string) =
        let naoDir = Path.Combine(workspaceRoot, ".nao")
        JsonSource(naoDir) :> IDefinitionSource

    /// Create a JSON source from an explicit directory
    let fromDirectory (dir: string) =
        JsonSource(dir) :> IDefinitionSource
