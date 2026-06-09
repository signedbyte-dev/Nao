namespace Nao.Loader

open System
open System.Text.Json
open Nao.Agents
open Nao.Core
open Nao.Eval

/// Functions for deserializing domain types directly from System.Text.Json elements.
[<RequireQualifiedAccess>]
module JsonRead =

    // ─── Primitive helpers ───

    let internal str (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

    let internal strOpt (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.String ->
            let s = v.GetString()
            if String.IsNullOrEmpty(s) then None else Some s
        | _ -> None

    let internal intVal (elem: JsonElement) (prop: string) (defaultVal: int) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
        | _ -> defaultVal

    let internal intOpt (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Number -> Some (v.GetInt32())
        | _ -> None

    let internal floatVal (elem: JsonElement) (prop: string) (defaultVal: float) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
        | _ -> defaultVal

    let internal strArray (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [ for item in v.EnumerateArray() do
                if item.ValueKind = JsonValueKind.String then
                    yield item.GetString() ]
        | _ -> []

    let internal objArray (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [ for item in v.EnumerateArray() do
                if item.ValueKind = JsonValueKind.Object then
                    yield item ]
        | _ -> []

    let internal subObj (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Object -> Some v
        | _ -> None

    let internal strMap (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Object ->
            [ for kv in v.EnumerateObject() do
                if kv.Value.ValueKind = JsonValueKind.String then
                    yield (kv.Name, kv.Value.GetString()) ]
            |> Map.ofList
        | _ -> Map.empty

    // ─── Domain type readers ───

    let promptExample (elem: JsonElement) : PromptExample =
        { Input = str elem "input"
          Output = str elem "output"
          Explanation = strOpt elem "explanation" }

    let outputFormat (elem: JsonElement) : OutputFormat =
        let format = str elem "output_format"
        let schema = strOpt elem "output_schema"
        match format with
        | "json" -> Json schema
        | "markdown" -> Markdown
        | "custom" -> Custom (schema |> Option.defaultValue "")
        | _ -> FreeText

    let prompt (elem: JsonElement) : Prompt =
        { Role = str elem "role"
          Objective = str elem "objective"
          DomainKnowledge = strArray elem "domain_knowledge"
          Constraints = strArray elem "constraints"
          Examples = objArray elem "examples" |> List.map promptExample
          OutputFormat = outputFormat elem
          Context = strArray elem "context" }

    let completionOptions (elem: JsonElement) : CompletionOptions =
        { Temperature = floatVal elem "temperature" CompletionOptions.Default.Temperature
          MaxTokens = intOpt elem "max_tokens"
          StopSequences = strArray elem "stop_sequences" }

    let evalCase (elem: JsonElement) : EvalCase =
        { Id = str elem "id"
          Description = str elem "description"
          Input = str elem "input"
          Expected = strOpt elem "expected"
          Tags = strArray elem "tags"
          Metadata = strMap elem "metadata" }

    let evalDataset (elem: JsonElement) : EvalDataset =
        { Name = str elem "name"
          Cases = objArray elem "cases" |> List.map evalCase }

    let agentDef (elem: JsonElement) : AgentDef =
        let promptElem = subObj elem "prompt" |> Option.defaultWith (fun () -> JsonDocument.Parse("{}").RootElement)
        let optionsElem = subObj elem "options" |> Option.defaultWith (fun () -> JsonDocument.Parse("{}").RootElement)
        { Name = str elem "name"
          Description = str elem "description"
          Provider = str elem "provider"
          Model = str elem "model"
          Prompt = prompt promptElem
          Tools = strArray elem "tools"
          SubAgents = strArray elem "sub_agents"
          Options = completionOptions optionsElem
          MaxRounds = intVal elem "max_rounds" 5 }

    let toolDef (elem: JsonElement) : ToolDef =
        { Name = str elem "name"
          Description = str elem "description"
          Command = str elem "command"
          Args = strArray elem "args" }

    let evaluatorRef (elem: JsonElement) : EvaluatorRef =
        { Type = str elem "type"
          Criteria = str elem "criteria"
          Scale = str elem "scale"
          Pattern = str elem "pattern"
          Keywords = strArray elem "keywords" }

    let evalSuiteDef (elem: JsonElement) : EvalSuiteDef =
        let evaluatorElem = subObj elem "evaluator" |> Option.defaultWith (fun () -> JsonDocument.Parse("{}").RootElement)
        { Name = str elem "name"
          Description = str elem "description"
          Agent = str elem "agent"
          Evaluator = evaluatorRef evaluatorElem
          Cases = objArray elem "cases" |> List.map evalCase }
