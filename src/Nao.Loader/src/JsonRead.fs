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

    let internal boolVal (elem: JsonElement) (prop: string) (defaultVal: bool) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.True -> true
        | true, v when v.ValueKind = JsonValueKind.False -> false
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
          Version = strOpt elem "version"
          Description = str elem "description"
          Provider = str elem "provider"
          Model = str elem "model"
          Prompt = prompt promptElem
          Tools = strArray elem "tools"
          SubAgents = strArray elem "sub_agents"
          Options = completionOptions optionsElem
          MaxRounds = intVal elem "max_rounds" 5
          IsAsync = boolVal elem "async" false
          Provenance = None }

    let private toolExecution (elem: JsonElement) (prefix: string) : ToolExecutionDef option =
        let mode =
            if prefix = "" then str elem "mode"
            else str elem (prefix + "_mode")
        let cmd =
            if prefix = "" then str elem "command"
            else str elem (prefix + "_command")
        let url =
            if prefix = "" then str elem "url"
            else str elem (prefix + "_url")
        let executor =
            if prefix = "" then str elem "executor"
            else str elem (prefix + "_executor")

        match mode with
        | "http" ->
            let httpMethod =
                if prefix = "" then str elem "method"
                else str elem (prefix + "_method")
            let method = if httpMethod = "" then "POST" else httpMethod
            let headers =
                if prefix = "" then strMap elem "headers"
                else strMap elem (prefix + "_headers")
            Some (ToolExecutionDef.Http (url, method, headers))
        | "custom" ->
            let config =
                if prefix = "" then strMap elem "config"
                else strMap elem (prefix + "_config")
            Some (ToolExecutionDef.Custom (executor, config))
        | "process" ->
            let args =
                if prefix = "" then strArray elem "args"
                else strArray elem (prefix + "_args")
            Some (ToolExecutionDef.Process (cmd, args))
        | _ ->
            // Default: if "command" is present, treat as process
            if cmd <> "" then
                let args =
                    if prefix = "" then strArray elem "args"
                    else strArray elem (prefix + "_args")
                Some (ToolExecutionDef.Process (cmd, args))
            elif url <> "" then
                let httpMethod =
                    if prefix = "" then str elem "method"
                    else str elem (prefix + "_method")
                let method = if httpMethod = "" then "POST" else httpMethod
                let headers =
                    if prefix = "" then strMap elem "headers"
                    else strMap elem (prefix + "_headers")
                Some (ToolExecutionDef.Http (url, method, headers))
            elif executor <> "" then
                let config =
                    if prefix = "" then strMap elem "config"
                    else strMap elem (prefix + "_config")
                Some (ToolExecutionDef.Custom (executor, config))
            else
                None

    let toolDef (elem: JsonElement) : ToolDef =
        let execution =
            toolExecution elem ""
            |> Option.defaultValue (ToolExecutionDef.Process (str elem "command", strArray elem "args"))
        { Name = str elem "name"
          Version = strOpt elem "version"
          Description = str elem "description"
          Execution = execution
          Runtime = str elem "runtime"
          OutputContentType = str elem "output_content_type"
          VerifyExecution = toolExecution elem "verify"
          RevertExecution = toolExecution elem "revert"
          Provenance = None }

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

    let constitutionRuleDef (elem: JsonElement) : ConstitutionRuleDef =
        { Id = str elem "id"
          Description = str elem "description"
          Category = str elem "category"
          Priority = intVal elem "priority" 0
          IsHardConstraint = boolVal elem "hard_constraint" true
          Pattern = str elem "pattern" }

    let constitutionDef (elem: JsonElement) : ConstitutionDef =
        { Name = str elem "name"
          Version = str elem "version"
          Rules = objArray elem "rules" |> List.map constitutionRuleDef }

    let toolParameter (elem: JsonElement) : ToolParameter =
        { Name = str elem "name"
          Type = str elem "type"
          Required = boolVal elem "required" false
          Description = str elem "description"
          Default = strOpt elem "default"
          Examples = strArray elem "examples" }

    let private parseCostCategory (s: string) =
        match s.ToLowerInvariant() with
        | "free" -> ToolCostCategory.Free
        | "low" | "cheap" -> ToolCostCategory.Cheap
        | "medium" | "moderate" -> ToolCostCategory.Moderate
        | "high" | "expensive" -> ToolCostCategory.Expensive
        | _ -> ToolCostCategory.Unknown

    let toolSchema (elem: JsonElement) : ToolSchema =
        { Name = str elem "name"
          Description = str elem "description"
          Category = strOpt elem "category"
          Parameters = objArray elem "parameters" |> List.map toolParameter
          ReturnDescription = strOpt elem "return_description"
          Examples = []
          IsSideEffectFree = boolVal elem "side_effect_free" false
          CostCategory = str elem "cost_category" |> (fun s -> if s = "" then "Cheap" else s) |> parseCostCategory
          Version = strOpt elem "version" }
