namespace Nao.Loader

open System.Diagnostics
open System.Text.RegularExpressions
open System.Threading.Tasks
open Nao.Agents
open Nao.Core
open Nao.Eval

/// Builds runnable domain objects from parsed definitions
module DefinitionBuilder =

    /// Build a Tool from a ToolDef (command-based execution)
    let buildTool (def: ToolDef) : Tool =
        { Name = def.Name
          Description = def.Description
          Execute = fun input ->
            task {
                let psi = ProcessStartInfo(def.Command)
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                for arg in def.Args do
                    psi.ArgumentList.Add(arg)
                psi.ArgumentList.Add(input)

                use proc = Process.Start(psi)
                let! output = proc.StandardOutput.ReadToEndAsync()
                do! proc.WaitForExitAsync()
                return output.TrimEnd()
            } }

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
