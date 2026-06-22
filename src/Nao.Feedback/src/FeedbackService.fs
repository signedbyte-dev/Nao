namespace Nao.Feedback

open System
open System.IO
open System.Threading.Tasks
open Nao.Agents
open Nao.Loader
open Nao.Persistence

/// High-level facade that ties the feedback loop together:
///   1. record each completed turn,
///   2. accept user feedback and turn it into persistent, active annotations,
///   3. overlay stored annotations onto tools/agents at load time (droppable → revert),
///   4. promote annotations into reviewed versions (Draft → Active → Deprecated).
///
/// Construct it with whichever stores you like (in-memory for tests, file-backed for the
/// running app) plus an analyzer. The default factories wire sensible implementations.
type FeedbackService
    (turnStore: ITurnStore,
     feedbackStore: IFeedbackStore,
     annotationStore: IAnnotationStore,
     versionStore: IVersionStore,
     suggestionStore: ISuggestionStore,
     analyzer: IFeedbackAnalyzer) =

    // ----- Turns & feedback --------------------------------------------------

    /// Persist a completed turn so feedback can later be analysed against it.
    member _.RecordTurnAsync(turn: TurnRecord) : Task = turnStore.SaveAsync turn

    /// Persist a raw feedback entry WITHOUT analysing it into annotations. Used by the
    /// event-storage consumer to record implicitly-captured feedback (whose detection
    /// happens at the producer); explicit feedback that should mutate behaviour goes
    /// through SubmitFeedbackAsync instead.
    member _.SaveFeedbackAsync(feedback: Feedback) : Task = feedbackStore.SaveAsync feedback

    /// Record user feedback, analyse it into annotations, persist them as ACTIVE (so the
    /// adjustment takes effect immediately and on every subsequent restart), and return
    /// the proposals that were applied.
    member _.SubmitFeedbackAsync(feedback: Feedback) : Task<AdjustmentProposal list> =
        task {
            do! feedbackStore.SaveAsync feedback
            match! turnStore.GetAsync feedback.TurnId with
            | None -> return []
            | Some turn ->
                let! proposals = analyzer.AnalyzeAsync turn feedback
                for p in proposals do
                    do! annotationStore.SaveAsync p.Annotation
                return proposals
        }

    /// Heuristically detect an *implicit* feedback signal in a user's message reacting to a
    /// prior turn, and persist it as a `Feedback` entry (sourced as conversation) bound to
    /// that turn. Returns the entry when a signal was found and a target turn was supplied.
    /// Unlike explicit feedback this does NOT auto-create annotations — it only feeds the
    /// cross-session suggestion pipeline, so an offhand remark never silently mutates a tool.
    member _.CaptureImplicitFeedbackAsync(turnId: string, sessionId: string, userId: string, text: string) : Task<Feedback option> =
        task {
            if String.IsNullOrWhiteSpace turnId then return None
            else
                match ConversationFeedback.detect text with
                | None -> return None
                | Some(sentiment, comment) ->
                    let feedback =
                        { Id = Guid.NewGuid()
                          TurnId = turnId
                          SessionId = sessionId
                          UserId = userId
                          Sentiment = sentiment
                          Comment = Some comment
                          CreatedAt = DateTimeOffset.UtcNow
                          Metadata = Map.empty |> FeedbackSource.stamp FeedbackSource.Conversation }
                    do! feedbackStore.SaveAsync feedback
                    return Some feedback
        }

    // ----- Annotations (live overlays) --------------------------------------

    /// Add an annotation directly (e.g. a manual user adjustment). Returns it as stored.
    member _.AddAnnotationAsync(annotation: Annotation) : Task<Annotation> =
        task {
            do! annotationStore.SaveAsync annotation
            return annotation
        }

    /// All stored annotations (active and disabled).
    member _.ListAnnotationsAsync() : Task<Annotation list> = annotationStore.GetAllAsync()

    /// Annotations for a specific tool/agent.
    member _.GetAnnotationsForTargetAsync(kind: AnnotationKind, targetName: string) : Task<Annotation list> =
        annotationStore.GetForTargetAsync(kind, targetName)

    /// Enable or disable an annotation. Disabling stops it being applied while keeping
    /// it on record so it can be re-enabled later.
    member _.SetAnnotationStatusAsync(id: Guid, status: AnnotationStatus) : Task<bool> =
        annotationStore.UpdateStatusAsync(id, status)

    /// Drop an annotation entirely — the legacy definition is restored on next load.
    member _.DropAnnotationAsync(id: Guid) : Task<bool> = annotationStore.DeleteAsync id

    /// Overlay all active annotations onto a tool list at load time. Tools without a
    /// matching annotation are returned unchanged.
    member _.ApplyToolAnnotationsAsync(tools: Tool list) : Task<Tool list> =
        task {
            let! all = annotationStore.GetAllAsync()
            return Annotations.applyToolAnnotations all tools
        }

    /// Overlay all active annotations onto an agent definition at build time.
    member _.ApplyAgentAnnotationsAsync(def: AgentDef) : Task<AgentDef> =
        task {
            let! all = annotationStore.GetAllAsync()
            return Annotations.applyAgentAnnotations all def
        }

    // ----- Versions (reviewed lifecycle) ------------------------------------

    /// Promote annotations for a target into a new Draft version. For JSON-sourced
    /// targets this materialises a real `<name>@<version>.json` definition file that
    /// coexists with the legacy one. If no annotation ids are supplied, all active
    /// annotations for the target are used.
    member this.PromoteAsync
        (kind: AnnotationKind, targetName: string,
         ?version: string, ?annotationIds: Guid list, ?provenance: ToolProvenance) : Task<VersionRecord> =
        task {
            let! all = annotationStore.GetForTargetAsync(kind, targetName)
            let selected =
                match annotationIds with
                | Some ids when not (List.isEmpty ids) -> all |> List.filter (fun a -> List.contains a.Id ids)
                | _ -> all |> List.filter (fun a -> a.Status = AnnotationStatus.Active)
            return! this.PromoteFromAnnotationsAsync(kind, targetName, selected, ?version = version, ?provenance = provenance)
        }

    /// Promote an explicit list of annotations into a new Draft version, without requiring
    /// them to be persisted in the annotation store. Used by the suggestion pipeline, whose
    /// proposed overlays are review-gated and intentionally kept out of the live store.
    member _.PromoteFromAnnotationsAsync
        (kind: AnnotationKind, targetName: string, annotations: Annotation list,
         ?version: string, ?provenance: ToolProvenance) : Task<VersionRecord> =
        task {
            let baseVersion = annotations |> List.tryPick (fun a -> a.BaseVersion)
            let label = defaultArg version (Versioning.next baseVersion)
            let prov =
                match provenance with
                | Some p -> Some p
                | None -> annotations |> List.tryPick (fun a -> a.Provenance)
            let location =
                match kind with
                | AnnotationKind.Tool -> Annotations.materializeToolVersion prov targetName label annotations
                | AnnotationKind.Agent -> Annotations.materializeAgentVersion prov targetName label annotations
            let loc, note =
                match location with
                | Ok p -> Some p, None
                | Error e -> None, Some e
            let record =
                { VersionRecord.Create(kind, targetName, label) with
                    SourceAnnotationIds = annotations |> List.map (fun a -> a.Id)
                    Location = loc
                    Notes = note }
            do! versionStore.SaveAsync record
            return record
        }

    /// All generated versions, across every target and status.
    member _.ListVersionsAsync() : Task<VersionRecord list> = versionStore.GetAllAsync()

    /// Versions generated for a particular tool/agent.
    member _.GetVersionsForTargetAsync(kind: AnnotationKind, targetName: string) : Task<VersionRecord list> =
        versionStore.GetForTargetAsync(kind, targetName)

    /// Confirm a Draft version as Active. When `replaceLegacy` is set, any other Active
    /// version of the same target is moved to Deprecated so the confirmed one supersedes it.
    member _.ConfirmVersionAsync(id: Guid, replaceLegacy: bool) : Task<bool> =
        task {
            let! ok = versionStore.UpdateStatusAsync(id, VersionStatus.Active)
            if ok && replaceLegacy then
                let! all = versionStore.GetAllAsync()
                match all |> List.tryFind (fun v -> v.Id = id) with
                | Some confirmed ->
                    for v in all do
                        if v.Id <> id
                           && v.Kind = confirmed.Kind
                           && v.TargetName = confirmed.TargetName
                           && v.Status = VersionStatus.Active then
                            let! _ = versionStore.UpdateStatusAsync(v.Id, VersionStatus.Deprecated)
                            ()
                | None -> ()
            return ok
        }

    /// Deprecate a version — it is retained but no longer offered by default. Dropping the
    /// underlying annotations reverts the target to its legacy behaviour entirely.
    member _.DeprecateVersionAsync(id: Guid) : Task<bool> =
        versionStore.UpdateStatusAsync(id, VersionStatus.Deprecated)

    // ----- Suggestions (cross-session review gate) --------------------------

    /// Aggregate ALL stored feedback (explicit + implicit) across every session into
    /// review-gated improvement suggestions, persisting any that are newly proposed. Targets
    /// that already have an open suggestion are skipped, so this is safe to call repeatedly
    /// ("enhance the system"). Returns the suggestions created by this call.
    member _.GenerateSuggestionsAsync() : Task<Suggestion list> =
        task {
            let! feedback = feedbackStore.GetAllAsync()
            let! existing = suggestionStore.GetAllAsync()
            // Resolve every turn referenced by feedback (the turn store has no GetAll).
            let turnIds = feedback |> List.map (fun f -> f.TurnId) |> List.distinct
            let resolved = ResizeArray<TurnRecord>()
            for tid in turnIds do
                match! turnStore.GetAsync tid with
                | Some t -> resolved.Add t
                | None -> ()
            let generated = SuggestionEngine.generate existing feedback (List.ofSeq resolved)
            for s in generated do
                do! suggestionStore.SaveAsync s
            return generated
        }

    /// All suggestions across every status.
    member _.ListSuggestionsAsync() : Task<Suggestion list> = suggestionStore.GetAllAsync()

    /// Suggestions in a particular review state.
    member _.GetSuggestionsByStatusAsync(status: SuggestionStatus) : Task<Suggestion list> =
        task {
            let! all = suggestionStore.GetAllAsync()
            return all |> List.filter (fun s -> s.Status = status)
        }

    /// Confirm a Proposed suggestion. The user approves the idea; it becomes eligible for
    /// candidate-workspace testing. No live definition is changed yet.
    member _.ConfirmSuggestionAsync(id: Guid) : Task<bool> =
        task {
            match! suggestionStore.GetAsync id with
            | Some s when s.Status = SuggestionStatus.Proposed ->
                return! suggestionStore.UpdateAsync { s with Status = SuggestionStatus.Confirmed }
            | _ -> return false
        }

    /// Reject a suggestion. It is retained for history but never applied; the same target may
    /// be proposed again by a future generation pass.
    member _.RejectSuggestionAsync(id: Guid) : Task<bool> =
        task {
            match! suggestionStore.GetAsync id with
            | Some s -> return! suggestionStore.UpdateAsync { s with Status = SuggestionStatus.Rejected }
            | None -> return false
        }

    /// Mark a confirmed suggestion as Applied, recording the version it produced. Called by
    /// the upgrade step once the improvement has been baked into the live workspace.
    member _.MarkSuggestionAppliedAsync(id: Guid, ?resultVersionId: Guid) : Task<bool> =
        task {
            match! suggestionStore.GetAsync id with
            | Some s ->
                return! suggestionStore.UpdateAsync { s with Status = SuggestionStatus.Applied; ResultVersionId = resultVersionId }
            | None -> return false
        }

    // ----- Factories ---------------------------------------------------------

    /// In-memory service (tests, ephemeral usage).
    static member InMemory() =
        FeedbackService(
            InMemoryTurnStore(),
            InMemoryFeedbackStore(),
            InMemoryAnnotationStore(),
            InMemoryVersionStore(),
            InMemorySuggestionStore(),
            HeuristicFeedbackAnalyzer())

    /// File-backed service rooted at <baseDir> (e.g. <NAO_DATA_DIR>/feedback).
    static member File(baseDir: string) =
        Directory.CreateDirectory baseDir |> ignore
        FeedbackService(
            FileTurnStore baseDir,
            FileFeedbackStore baseDir,
            FileAnnotationStore baseDir,
            FileVersionStore baseDir,
            FileSuggestionStore baseDir,
            HeuristicFeedbackAnalyzer())

    /// Database-backed service over any ADO.NET provider (SQLite, PostgreSQL, SQL Server, ...).
    /// Tables are created on demand (CREATE TABLE IF NOT EXISTS).
    static member Database(factory: IDbConnectionFactory) =
        FeedbackService(
            AdoTurnStore factory,
            AdoFeedbackStore factory,
            AdoAnnotationStore factory,
            AdoVersionStore factory,
            AdoSuggestionStore factory,
            HeuristicFeedbackAnalyzer())

    /// Select the backend with a single knob, mirroring Nao.Persistence's PersistenceMode:
    ///   InMemory (tests) | File baseDir (local files) | Database factory (any ADO.NET store).
    static member Create(mode: PersistenceMode) =
        match mode with
        | PersistenceMode.InMemory -> FeedbackService.InMemory()
        | PersistenceMode.File baseDir -> FeedbackService.File baseDir
        | PersistenceMode.Database factory -> FeedbackService.Database factory
