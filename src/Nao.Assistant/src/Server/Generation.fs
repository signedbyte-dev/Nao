namespace Nao.Assistant

open System
open System.IO
open System.Net.WebSockets
open System.Net.Sockets
open System.Data.Common
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Persistence
open Nao.Feedback
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

/// LLM-backed generation of tool and agent JSON definitions from a natural-language
/// requirement. Output is validated as JSON before being returned for user review.
module Generation =

    /// Pull a single JSON object out of an LLM response (tolerates markdown fences/prose).
    let extractJson (raw: string) : string =
        let stripped = Regex.Replace(raw.Trim(), "```[a-zA-Z]*", "").Trim()
        let i = stripped.IndexOf('{')
        let j = stripped.LastIndexOf('}')
        if i >= 0 && j > i then stripped.Substring(i, j - i + 1) else stripped

    let private complete (provider: ILlmProvider) (system: string) (user: string) = task {
        let conv = [ { Role = Role.System; Content = system }; { Role = Role.User; Content = user } ]
        let! result = provider.CompleteAsync conv { CompletionOptions.Default with Temperature = 0.2 }
        return result.Content
    }

    let private toolSystem =
        "You generate ONE JSON tool definition for the Nao agent framework. "
        + "Output ONLY the JSON object — no prose, no markdown fences.\n"
        + "Process/shell tool schema (flat keys):\n"
        + "{\n  \"name\": \"snake_case_unique_name\",\n  \"description\": \"what it does and the expected input format\",\n"
        + "  \"command\": \"executable\",\n  \"args\": [\"fixed\", \"args\"],\n  \"output_content_type\": \"text\"\n}\n"
        + "HTTP tool schema instead:\n"
        + "{\n  \"name\": \"...\",\n  \"description\": \"...\",\n  \"mode\": \"http\",\n  \"method\": \"GET\",\n  \"url\": \"https://...\",\n  \"output_content_type\": \"application/json\"\n}\n"
        + "Rules: choose exactly ONE style (command OR http). The runtime appends the user's input string as the final CLI argument. "
        + "Use only widely-available commands (bash, curl, echo, date, jq, grep, ls, cat, python3). Keep it read-only unless writes are required."

    let private agentSystem (availableTools: string list) =
        "You generate ONE JSON agent (workflow) definition for the Nao framework. "
        + "Output ONLY the JSON object — no prose, no markdown fences.\n"
        + "Schema:\n{\n  \"name\": \"kebab-or-snake-name\",\n  \"description\": \"what task/workflow this agent accomplishes\",\n"
        + "  \"prompt\": {\n    \"role\": \"You are ...\",\n    \"objective\": \"...\",\n    \"constraints\": [\"...\"]\n  },\n"
        + "  \"tools\": [\"tool_name\"],\n  \"maxRounds\": 10\n}\n"
        + "Choose tools ONLY from this available list: " + String.Join(", ", availableTools)

    let private nameOf (json: string) : string =
        try
            use doc = JsonDocument.Parse json
            match doc.RootElement.TryGetProperty "name" with
            | true, v -> v.GetString()
            | _ -> ""
        with _ -> ""

    let private prettyOrRaw (json: string) : string =
        try
            use doc = JsonDocument.Parse json
            JsonSerializer.Serialize(doc.RootElement, JsonSerializerOptions(WriteIndented = true))
        with _ -> json

    let generate (provider: ILlmProvider) (system: string) (requirement: string) : Task<Result<GeneratedDefinitionDto, string>> =
        task {
            try
                let! raw = complete provider system requirement
                let json = extractJson raw
                let name = nameOf json
                if String.IsNullOrWhiteSpace name then
                    return Error "The model did not return a valid definition (missing name)."
                else
                    return Ok { Name = name; Json = prettyOrRaw json }
            with ex ->
                return Error ex.Message
        }

    let generateTool provider requirement = generate provider toolSystem requirement
    let generateAgent provider tools requirement = generate provider (agentSystem tools) requirement


