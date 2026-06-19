namespace Nao.Feedback

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Nao.Agents

/// Analyses a turn together with the user's feedback and proposes tool adjustments.
/// This is the pluggable "brain" of the system — a heuristic implementation ships
/// here, but an LLM-backed analyzer can be substituted without changing the pipeline.
type IFeedbackAnalyzer =
    abstract member AnalyzeAsync: TurnRecord -> Feedback -> Task<AdjustmentProposal list>

module Versioning =
    /// Compute the next version label after the given one.
    /// None -> "v2"; trailing integers are incremented ("v2" -> "v3").
    let next (current: string option) : string =
        match current with
        | None -> "v2"
        | Some v ->
            let m = Regex.Match(v, @"(\d+)$")
            if m.Success then
                let n = int m.Value + 1
                v.Substring(0, m.Index) + string n
            else v + ".1"

/// A simple, deterministic analyzer: on negative feedback it proposes an active
/// annotation for every tool used in the turn, appending the user's comment as guidance.
/// When no tools were used, it proposes an annotation on the agent itself so the guidance
/// is injected into the agent's constraints. These annotations take effect immediately and
/// can later be promoted into reviewed versions.
type HeuristicFeedbackAnalyzer() =
    interface IFeedbackAnalyzer with
        member _.AnalyzeAsync (turn: TurnRecord) (feedback: Feedback) =
            task {
                match feedback.Sentiment with
                | FeedbackSentiment.Negative ->
                    let guidance =
                        match feedback.Comment with
                        | Some c when not (String.IsNullOrWhiteSpace c) ->
                            sprintf "Guidance from user feedback: %s" c
                        | _ -> "Adjust behaviour in response to negative user feedback on a prior turn."
                    let source = sprintf "feedback:%s" (feedback.Id.ToString())
                    let toolProposals =
                        turn.ToolCalls
                        |> List.distinctBy (fun tc -> tc.Name, tc.Version)
                        |> List.map (fun tc ->
                            let annotation =
                                { Annotation.ForTool(tc.Name) with
                                    BaseVersion = tc.Version
                                    DescriptionAppend = Some guidance
                                    Source = source
                                    Provenance = tc.Provenance
                                    Reason = feedback.Comment }
                            { Annotation = annotation
                              Rationale =
                                sprintf "Negative feedback on turn %s: annotate tool '%s' with added guidance."
                                    turn.TurnId tc.Name })
                    let proposals =
                        if List.isEmpty toolProposals then
                            let annotation =
                                { Annotation.ForAgent(turn.AgentName) with
                                    BaseVersion = turn.AgentVersion
                                    GuidanceAppend = Some guidance
                                    Source = source
                                    Reason = feedback.Comment }
                            [ { Annotation = annotation
                                Rationale =
                                  sprintf "Negative feedback on turn %s: annotate agent '%s' with added guidance."
                                      turn.TurnId turn.AgentName } ]
                        else toolProposals
                    return proposals
                | _ ->
                    return []
            }

/// Cross-session aggregation: turns the entire body of stored feedback (explicit AND
/// implicit) into review-gated `Suggestion`s. This is the deterministic counterpart to the
/// per-turn analyzer above — instead of immediate overlays, it produces durable improvement
/// proposals the user reviews before anything changes.
module SuggestionEngine =

    /// One target a negative feedback entry points at, with the context needed to build a
    /// concrete improvement overlay.
    type private TargetHit =
        { Kind: AnnotationKind
          Name: string
          BaseVersion: string option
          Provenance: ToolProvenance option
          Feedback: Feedback }

    /// Resolve the tool/agent targets implicated by one feedback entry against its turn.
    let private hitsFor (turnMap: Map<string, TurnRecord>) (f: Feedback) : TargetHit list =
        match turnMap.TryFind f.TurnId with
        | None -> []
        | Some turn ->
            match turn.ToolCalls with
            | [] ->
                [ { Kind = AnnotationKind.Agent
                    Name = turn.AgentName
                    BaseVersion = turn.AgentVersion
                    Provenance = None
                    Feedback = f } ]
            | calls ->
                calls
                |> List.distinctBy (fun tc -> tc.Name, tc.Version)
                |> List.map (fun tc ->
                    { Kind = AnnotationKind.Tool
                      Name = tc.Name
                      BaseVersion = tc.Version
                      Provenance = tc.Provenance
                      Feedback = f })

    /// Generate new Proposed suggestions from all feedback. `existing` is consulted so a
    /// target that already has an open (Proposed/Confirmed/Applied) suggestion is not
    /// duplicated — only Rejected suggestions allow a fresh proposal. Only negative feedback
    /// drives suggestions; positive/neutral signals are recorded but never propose changes.
    let generate (existing: Suggestion list) (feedbacks: Feedback list) (turns: TurnRecord list) : Suggestion list =
        let turnMap = turns |> List.map (fun t -> t.TurnId, t) |> Map.ofList
        let covered =
            existing
            |> List.filter (fun s -> s.Status <> SuggestionStatus.Rejected)
            |> List.map (fun s -> s.Kind, s.TargetName)
            |> Set.ofList
        feedbacks
        |> List.filter (fun f -> f.Sentiment = FeedbackSentiment.Negative)
        |> List.collect (hitsFor turnMap)
        |> List.groupBy (fun h -> h.Kind, h.Name)
        |> List.filter (fun (key, _) -> not (Set.contains key covered))
        |> List.map (fun ((kind, name), hits) ->
            let feedbackEntries = hits |> List.map (fun h -> h.Feedback)
            let comments =
                feedbackEntries
                |> List.choose (fun f -> f.Comment)
                |> List.filter (fun c -> not (String.IsNullOrWhiteSpace c))
                |> List.distinct
            let guidance =
                if List.isEmpty comments then
                    "Adjust behaviour in response to repeated negative user feedback."
                else
                    "Guidance distilled from user feedback:\n" + (comments |> List.map (sprintf "- %s") |> String.concat "\n")
            let baseVersion = hits |> List.tryPick (fun h -> h.BaseVersion)
            let provenance = hits |> List.tryPick (fun h -> h.Provenance)
            let feedbackIds = feedbackEntries |> List.map (fun f -> f.Id) |> List.distinct
            let sessions = feedbackEntries |> List.map (fun f -> f.SessionId) |> List.distinct
            let annotation =
                match kind with
                | AnnotationKind.Tool ->
                    { Annotation.ForTool(name) with
                        BaseVersion = baseVersion
                        DescriptionAppend = Some guidance
                        Source = "suggestion"
                        Provenance = provenance
                        Reason = Some guidance }
                | AnnotationKind.Agent ->
                    { Annotation.ForAgent(name) with
                        BaseVersion = baseVersion
                        GuidanceAppend = Some guidance
                        Source = "suggestion"
                        Reason = Some guidance }
            let kindLabel = match kind with AnnotationKind.Tool -> "tool" | AnnotationKind.Agent -> "agent"
            { Suggestion.Improve(kind, name) with
                ProposedAnnotation = Some annotation
                Rationale =
                    sprintf "%d negative signal(s) across %d session(s) on %s '%s'. %s"
                        (List.length feedbackEntries) (List.length sessions) kindLabel name guidance
                SupportingFeedbackIds = feedbackIds
                SupportingSessions = sessions
                NegativeCount = List.length feedbackEntries })
        |> List.sortByDescending (fun s -> s.NegativeCount)

