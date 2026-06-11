namespace Nao.Loader

open System
open System.Diagnostics
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Nao.Agents
open Nao.Core
open Nao.Eval

/// Extension point for custom tool execution strategies (gRPC, MCP, etc.)
type IToolExecutor =
    /// Execute a tool with the given input and config, return (success, output)
    abstract member ExecuteAsync: input: string * config: Map<string, string> -> Task<Result<string, string>>

/// Builds runnable domain objects from parsed definitions
module DefinitionBuilder =

    /// Registry of custom tool executors (populated at startup by consumers)
    let private executors = System.Collections.Concurrent.ConcurrentDictionary<string, IToolExecutor>()

    /// Register a custom tool executor by name
    let registerExecutor (name: string) (executor: IToolExecutor) =
        executors.[name] <- executor

    /// Remove a custom tool executor
    let removeExecutor (name: string) =
        executors.TryRemove(name) |> ignore

    /// Run a process with arguments and return (exitCode, stdout)
    let private runProcess (cmd: string) (args: string list) : Task<int * string> =
        task {
            let psi = ProcessStartInfo(cmd)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            for arg in args do
                psi.ArgumentList.Add(arg)
            use proc = Process.Start(psi)
            let! output = proc.StandardOutput.ReadToEndAsync()
            do! proc.WaitForExitAsync()
            return (proc.ExitCode, output.TrimEnd())
        }

    /// Execute an HTTP call and return the response body
    let private runHttp (url: string) (httpMethod: string) (headers: Map<string, string>) (input: string) : Task<Result<string, string>> =
        task {
            use client = new HttpClient()
            for kv in headers do
                client.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value) |> ignore
            let! response =
                match httpMethod.ToUpperInvariant() with
                | "GET" -> client.GetAsync(url + "?input=" + Uri.EscapeDataString(input))
                | "PUT" -> client.PutAsync(url, new StringContent(input, Text.Encoding.UTF8, "application/json"))
                | "DELETE" -> client.DeleteAsync(url + "?input=" + Uri.EscapeDataString(input))
                | _ -> client.PostAsync(url, new StringContent(input, Text.Encoding.UTF8, "application/json"))
            let! body = response.Content.ReadAsStringAsync()
            if response.IsSuccessStatusCode then
                return Ok body
            else
                return Error (sprintf "HTTP %d: %s" (int response.StatusCode) body)
        }

    /// Execute a ToolExecutionDef with the given arguments
    let private executeDefAsync (exec: ToolExecutionDef) (args: string list) : Task<Result<string, string>> =
        task {
            match exec with
            | ToolExecutionDef.Process (cmd, fixedArgs) ->
                let! (exitCode, output) = runProcess cmd (fixedArgs @ args)
                if exitCode = 0 then return Ok output
                else return Error (sprintf "Process exited with code %d: %s" exitCode output)
            | ToolExecutionDef.Http (url, httpMethod, headers) ->
                let input = args |> String.concat " "
                return! runHttp url httpMethod headers input
            | ToolExecutionDef.Custom (executorName, config) ->
                match executors.TryGetValue(executorName) with
                | true, executor ->
                    let input = args |> String.concat " "
                    return! executor.ExecuteAsync(input, config)
                | false, _ ->
                    return Error (sprintf "Custom executor '%s' not registered" executorName)
        }

    /// Build a Tool from a ToolDef
    let buildTool (def: ToolDef) : Tool =
        let verify =
            match def.VerifyExecution with
            | None -> None
            | Some verifyExec ->
                Some (fun (input: string) (output: string) ->
                    task {
                        let! result = executeDefAsync verifyExec [input; output]
                        return result |> Result.map ignore
                    })
        let revert =
            match def.RevertExecution with
            | None -> None
            | Some revertExec ->
                Some (fun (ctx: RevertContext) ->
                    task {
                        let! result = executeDefAsync revertExec [ctx.Input; ctx.Output]
                        return result |> Result.map ignore
                    })
        let contentType =
            if String.IsNullOrEmpty(def.OutputContentType) then ContentMeta.Text
            else ContentMeta.Of def.OutputContentType
        { Name = def.Name
          Description = def.Description
          Execute = fun input -> task {
            let! result = executeDefAsync def.Execution [input]
            return
                match result with
                | Ok output -> output
                | Error err -> sprintf "[Error] %s" err
          }
          OutputContentType = contentType
          Verify = verify
          Revert = revert }

    /// Build an OrchestratorConfig from an AgentDef
    let buildOrchestratorConfig
        (provider: ILlmProvider)
        (tools: Tool list)
        (subAgents: IAgent list)
        (def: AgentDef)
        : OrchestratorConfig =
        { Provider = provider
          Tools = tools
          SubAgents = subAgents
          Prompt = def.Prompt
          Options = def.Options
          MaxRounds = def.MaxRounds
          EventSink = AgentEventSink.none
          Memory = OrchestratorMemoryConfig.None }

    /// Build an IAgent from an AgentDef
    let buildAgent
        (provider: ILlmProvider)
        (tools: Tool list)
        (subAgents: IAgent list)
        (def: AgentDef)
        : IAgent =
        let config = buildOrchestratorConfig provider tools subAgents def
        Orchestrator.createWithConfig config

    /// Build an EvalDataset from an EvalSuiteDef
    let buildEvalDataset (def: EvalSuiteDef) : EvalDataset =
        { Name = def.Name
          Cases = def.Cases }

    /// Parse a category string into a RuleCategory
    let private parseRuleCategory (s: string) : RuleCategory =
        match s with
        | "Safety" -> RuleCategory.Safety
        | "Privacy" -> RuleCategory.Privacy
        | "Behavioral" -> RuleCategory.Behavioral
        | "Format" -> RuleCategory.Format
        | other ->
            if other.StartsWith("Domain:") then
                RuleCategory.Domain (other.Substring(7))
            else
                RuleCategory.Domain other

    /// Build a ConstitutionRule from a ConstitutionRuleDef.
    /// The Pattern field becomes a regex-based Check function.
    let buildConstitutionRule (def: ConstitutionRuleDef) : ConstitutionRule =
        let check =
            if System.String.IsNullOrWhiteSpace(def.Pattern) then
                fun _ -> false
            else
                let regex = Regex(def.Pattern, RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
                fun (content: string) -> regex.IsMatch(content)
        { Id = def.Id
          Description = def.Description
          Category = parseRuleCategory def.Category
          Priority = def.Priority
          IsHardConstraint = def.IsHardConstraint
          Check = check }

    /// Build a Constitution from a ConstitutionDef
    let buildConstitution (def: ConstitutionDef) : Constitution =
        { Name = def.Name
          Version = def.Version
          Rules = def.Rules |> List.map buildConstitutionRule |> List.sortByDescending (fun r -> r.Priority)
          Preamble = None }

    /// Build a merged Constitution from multiple ConstitutionDefs
    let buildMergedConstitution (defs: ConstitutionDef list) : Constitution option =
        match defs with
        | [] -> None
        | [ single ] -> Some (buildConstitution single)
        | multiple ->
            let allRules = multiple |> List.collect (fun d -> d.Rules) |> List.map buildConstitutionRule
            Some
                { Name = multiple |> List.map (fun d -> d.Name) |> String.concat "+"
                  Version = "merged"
                  Rules = allRules |> List.sortByDescending (fun r -> r.Priority)
                  Preamble = None }
