namespace Nao.Feedback

open System
open System.IO
open System.Collections.Generic
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Serialization
open Nao.Persistence

/// Shared JSON options for feedback artifacts. The F# converter handles options,
/// records, discriminated unions, and maps so everything round-trips cleanly.
module FeedbackJson =
    let options =
        let o = JsonSerializerOptions(WriteIndented = false)
        o.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.InternalTag ||| JsonUnionEncoding.UnwrapFieldlessTags))
        o

    let serialize (value: 'a) : string = JsonSerializer.Serialize(value, options)
    let deserialize<'a> (s: string) : 'a = JsonSerializer.Deserialize<'a>(s, options)

    /// Pretty-printed variant for human-editable artifacts (e.g. emitted tool JSON).
    let indentedOptions =
        let o = JsonSerializerOptions(WriteIndented = true)
        o.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.InternalTag ||| JsonUnionEncoding.UnwrapFieldlessTags))
        o

    let serializeIndented (value: 'a) : string = JsonSerializer.Serialize(value, indentedOptions)

// ─── Store interfaces ───

/// Persists completed turn records so feedback can be analysed against them later.
type ITurnStore =
    abstract member SaveAsync: TurnRecord -> Task
    abstract member GetAsync: turnId: string -> Task<TurnRecord option>
    abstract member GetForSessionAsync: sessionId: string -> Task<TurnRecord list>

/// Persists user feedback entries.
type IFeedbackStore =
    abstract member SaveAsync: Feedback -> Task
    abstract member GetForTurnAsync: turnId: string -> Task<Feedback list>
    abstract member GetForSessionAsync: sessionId: string -> Task<Feedback list>
    /// Every feedback entry across all sessions — the input to cross-session aggregation.
    abstract member GetAllAsync: unit -> Task<Feedback list>

/// Persists cross-session improvement suggestions through their review lifecycle.
type ISuggestionStore =
    abstract member SaveAsync: Suggestion -> Task
    abstract member GetAllAsync: unit -> Task<Suggestion list>
    abstract member GetAsync: id: Guid -> Task<Suggestion option>
    /// Replace a suggestion by id (status changes, result version). Returns false if absent.
    abstract member UpdateAsync: Suggestion -> Task<bool>

/// Persists annotations — persistent runtime overlays on tools/agents. Supports
/// update (status change) and delete (drop → revert to legacy).
type IAnnotationStore =
    abstract member SaveAsync: Annotation -> Task
    abstract member GetAllAsync: unit -> Task<Annotation list>
    abstract member GetForTargetAsync: kind: AnnotationKind * targetName: string -> Task<Annotation list>
    abstract member UpdateStatusAsync: id: Guid * status: AnnotationStatus -> Task<bool>
    abstract member DeleteAsync: id: Guid -> Task<bool>

/// Persists generated tool/agent versions and tracks their review lifecycle.
type IVersionStore =
    abstract member SaveAsync: VersionRecord -> Task
    abstract member GetAllAsync: unit -> Task<VersionRecord list>
    abstract member GetForTargetAsync: kind: AnnotationKind * targetName: string -> Task<VersionRecord list>
    abstract member UpdateStatusAsync: id: Guid * status: VersionStatus -> Task<bool>

// ─── In-memory implementations ───

type InMemoryTurnStore() =
    let items = Dictionary<string, TurnRecord>()
    let sync = obj ()
    interface ITurnStore with
        member _.SaveAsync(turn) =
            lock sync (fun () -> items.[turn.TurnId] <- turn)
            Task.CompletedTask
        member _.GetAsync(turnId) =
            lock sync (fun () ->
                match items.TryGetValue turnId with
                | true, v -> Some v
                | _ -> None)
            |> Task.FromResult
        member _.GetForSessionAsync(sessionId) =
            lock sync (fun () ->
                items.Values |> Seq.filter (fun t -> t.SessionId = sessionId) |> List.ofSeq)
            |> Task.FromResult

type InMemoryFeedbackStore() =
    let items = ResizeArray<Feedback>()
    let sync = obj ()
    interface IFeedbackStore with
        member _.SaveAsync(feedback) =
            lock sync (fun () -> items.Add feedback)
            Task.CompletedTask
        member _.GetForTurnAsync(turnId) =
            lock sync (fun () -> items |> Seq.filter (fun f -> f.TurnId = turnId) |> List.ofSeq)
            |> Task.FromResult
        member _.GetForSessionAsync(sessionId) =
            lock sync (fun () -> items |> Seq.filter (fun f -> f.SessionId = sessionId) |> List.ofSeq)
            |> Task.FromResult
        member _.GetAllAsync() =
            lock sync (fun () -> List.ofSeq items) |> Task.FromResult

type InMemorySuggestionStore() =
    let items = ResizeArray<Suggestion>()
    let sync = obj ()
    interface ISuggestionStore with
        member _.SaveAsync(suggestion) =
            lock sync (fun () -> items.Add suggestion)
            Task.CompletedTask
        member _.GetAllAsync() =
            lock sync (fun () -> List.ofSeq items) |> Task.FromResult
        member _.GetAsync(id) =
            lock sync (fun () -> items |> Seq.tryFind (fun s -> s.Id = id)) |> Task.FromResult
        member _.UpdateAsync(suggestion) =
            lock sync (fun () ->
                match items.FindIndex(fun s -> s.Id = suggestion.Id) with
                | -1 -> false
                | i ->
                    items.[i] <- suggestion
                    true)
            |> Task.FromResult

type InMemoryAnnotationStore() =
    let items = ResizeArray<Annotation>()
    let sync = obj ()
    interface IAnnotationStore with
        member _.SaveAsync(annotation) =
            lock sync (fun () -> items.Add annotation)
            Task.CompletedTask
        member _.GetAllAsync() =
            lock sync (fun () -> List.ofSeq items) |> Task.FromResult
        member _.GetForTargetAsync(kind, targetName) =
            lock sync (fun () ->
                items |> Seq.filter (fun a -> a.Kind = kind && a.TargetName = targetName) |> List.ofSeq)
            |> Task.FromResult
        member _.UpdateStatusAsync(id, status) =
            lock sync (fun () ->
                match items.FindIndex(fun a -> a.Id = id) with
                | -1 -> false
                | i ->
                    items.[i] <- { items.[i] with Status = status }
                    true)
            |> Task.FromResult
        member _.DeleteAsync(id) =
            lock sync (fun () -> items.RemoveAll(fun a -> a.Id = id) > 0)
            |> Task.FromResult

type InMemoryVersionStore() =
    let items = ResizeArray<VersionRecord>()
    let sync = obj ()
    interface IVersionStore with
        member _.SaveAsync(version) =
            lock sync (fun () -> items.Add version)
            Task.CompletedTask
        member _.GetAllAsync() =
            lock sync (fun () -> List.ofSeq items) |> Task.FromResult
        member _.GetForTargetAsync(kind, targetName) =
            lock sync (fun () ->
                items |> Seq.filter (fun v -> v.Kind = kind && v.TargetName = targetName) |> List.ofSeq)
            |> Task.FromResult
        member _.UpdateStatusAsync(id, status) =
            lock sync (fun () ->
                match items.FindIndex(fun v -> v.Id = id) with
                | -1 -> false
                | i ->
                    items.[i] <- { items.[i] with Status = status }
                    true)
            |> Task.FromResult

// ─── File (JSONL) implementations ───

/// Append-only JSONL helpers shared by the file-backed stores.
module private Jsonl =
    let private sync = obj ()

    let append (path: string) (line: string) =
        lock sync (fun () ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.AppendAllText(path, line + "\n"))

    let readAll<'a> (path: string) : 'a list =
        if not (File.Exists path) then []
        else
            File.ReadAllLines path
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> Array.choose (fun l ->
                try Some (FeedbackJson.deserialize<'a> l) with _ -> None)
            |> Array.toList

    /// Rewrite the whole file from a list (used for update/delete on mutable stores).
    let writeAll<'a> (path: string) (items: 'a list) =
        lock sync (fun () ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let lines = items |> List.map FeedbackJson.serialize
            File.WriteAllText(path, String.Join("\n", lines) + (if List.isEmpty lines then "" else "\n")))

/// Turn records persisted as JSONL at <baseDir>/turns.jsonl.
type FileTurnStore(baseDir: string) =
    let path = Path.Combine(baseDir, "turns.jsonl")
    interface ITurnStore with
        member _.SaveAsync(turn) =
            Jsonl.append path (FeedbackJson.serialize turn)
            Task.CompletedTask
        member _.GetAsync(turnId) =
            Jsonl.readAll<TurnRecord> path
            |> List.rev
            |> List.tryFind (fun t -> t.TurnId = turnId)
            |> Task.FromResult
        member _.GetForSessionAsync(sessionId) =
            Jsonl.readAll<TurnRecord> path
            |> List.filter (fun t -> t.SessionId = sessionId)
            |> Task.FromResult

/// Feedback persisted as JSONL at <baseDir>/feedback.jsonl.
type FileFeedbackStore(baseDir: string) =
    let path = Path.Combine(baseDir, "feedback.jsonl")
    interface IFeedbackStore with
        member _.SaveAsync(feedback) =
            Jsonl.append path (FeedbackJson.serialize feedback)
            Task.CompletedTask
        member _.GetForTurnAsync(turnId) =
            Jsonl.readAll<Feedback> path
            |> List.filter (fun f -> f.TurnId = turnId)
            |> Task.FromResult
        member _.GetForSessionAsync(sessionId) =
            Jsonl.readAll<Feedback> path
            |> List.filter (fun f -> f.SessionId = sessionId)
            |> Task.FromResult
        member _.GetAllAsync() =
            Jsonl.readAll<Feedback> path |> Task.FromResult

/// Suggestions persisted as JSONL at <baseDir>/suggestions.jsonl. Update rewrites the
/// file so status transitions (Proposed -> Confirmed/Rejected/Applied) survive restarts.
type FileSuggestionStore(baseDir: string) =
    let path = Path.Combine(baseDir, "suggestions.jsonl")
    let sync = obj ()
    interface ISuggestionStore with
        member _.SaveAsync(suggestion) =
            Jsonl.append path (FeedbackJson.serialize suggestion)
            Task.CompletedTask
        member _.GetAllAsync() =
            Jsonl.readAll<Suggestion> path |> Task.FromResult
        member _.GetAsync(id) =
            Jsonl.readAll<Suggestion> path
            |> List.tryFind (fun s -> s.Id = id)
            |> Task.FromResult
        member _.UpdateAsync(suggestion) =
            lock sync (fun () ->
                let all = Jsonl.readAll<Suggestion> path
                if all |> List.exists (fun s -> s.Id = suggestion.Id) then
                    all
                    |> List.map (fun s -> if s.Id = suggestion.Id then suggestion else s)
                    |> Jsonl.writeAll path
                    true
                else false)
            |> Task.FromResult

/// Annotations persisted as JSONL at <baseDir>/annotations.jsonl. Update and delete
/// rewrite the file so droppable, editable overlays survive restarts.
type FileAnnotationStore(baseDir: string) =
    let path = Path.Combine(baseDir, "annotations.jsonl")
    let sync = obj ()
    interface IAnnotationStore with
        member _.SaveAsync(annotation) =
            Jsonl.append path (FeedbackJson.serialize annotation)
            Task.CompletedTask
        member _.GetAllAsync() =
            Jsonl.readAll<Annotation> path |> Task.FromResult
        member _.GetForTargetAsync(kind, targetName) =
            Jsonl.readAll<Annotation> path
            |> List.filter (fun a -> a.Kind = kind && a.TargetName = targetName)
            |> Task.FromResult
        member _.UpdateStatusAsync(id, status) =
            lock sync (fun () ->
                let all = Jsonl.readAll<Annotation> path
                if all |> List.exists (fun a -> a.Id = id) then
                    all
                    |> List.map (fun a -> if a.Id = id then { a with Status = status } else a)
                    |> Jsonl.writeAll path
                    true
                else false)
            |> Task.FromResult
        member _.DeleteAsync(id) =
            lock sync (fun () ->
                let all = Jsonl.readAll<Annotation> path
                let remaining = all |> List.filter (fun a -> a.Id <> id)
                if List.length remaining <> List.length all then
                    Jsonl.writeAll path remaining
                    true
                else false)
            |> Task.FromResult

/// Generated versions persisted as JSONL at <baseDir>/versions.jsonl.
type FileVersionStore(baseDir: string) =
    let path = Path.Combine(baseDir, "versions.jsonl")
    let sync = obj ()
    interface IVersionStore with
        member _.SaveAsync(version) =
            Jsonl.append path (FeedbackJson.serialize version)
            Task.CompletedTask
        member _.GetAllAsync() =
            Jsonl.readAll<VersionRecord> path |> Task.FromResult
        member _.GetForTargetAsync(kind, targetName) =
            Jsonl.readAll<VersionRecord> path
            |> List.filter (fun v -> v.Kind = kind && v.TargetName = targetName)
            |> Task.FromResult
        member _.UpdateStatusAsync(id, status) =
            lock sync (fun () ->
                let all = Jsonl.readAll<VersionRecord> path
                if all |> List.exists (fun v -> v.Id = id) then
                    all
                    |> List.map (fun v -> if v.Id = id then { v with Status = status } else v)
                    |> Jsonl.writeAll path
                    true
                else false)
            |> Task.FromResult

// ─── Database (ADO.NET) implementations ───

/// Generic JSON-payload table helpers shared by the ADO-backed feedback stores.
///
/// Each feedback artifact is stored as a single row: a string primary key plus a
/// JSON payload column serialized with <see cref="FeedbackJson"/>. Filtering and
/// status edits load the (small) artifact set and project in F#, mirroring the
/// JSONL file stores. The schema is portable (CREATE TABLE IF NOT EXISTS) and works
/// against any ADO.NET provider supplied via <see cref="IDbConnectionFactory"/>.
module private AdoPayload =

    let ensure (factory: IDbConnectionFactory) (table: string) : Task =
        Ado.executeNonQuery
            factory
            (sprintf "CREATE TABLE IF NOT EXISTS %s (item_id TEXT NOT NULL PRIMARY KEY, payload TEXT NOT NULL)" table)
            []
        :> Task

    let getAll<'a> (factory: IDbConnectionFactory) (table: string) : Task<'a list> =
        task {
            do! ensure factory table
            return!
                Ado.query
                    factory
                    (sprintf "SELECT payload FROM %s" table)
                    []
                    (fun r -> FeedbackJson.deserialize<'a> (Ado.getString r "payload"))
        }

    /// Insert-or-replace a single artifact by primary key (DELETE + INSERT in one tx).
    let upsert (factory: IDbConnectionFactory) (table: string) (id: string) (item: 'a) : Task =
        task {
            do! ensure factory table
            do!
                Ado.executeTransaction
                    factory
                    [ sprintf "DELETE FROM %s WHERE item_id = @id" table, [ "@id", box id ]
                      sprintf "INSERT INTO %s (item_id, payload) VALUES (@id, @p)" table,
                      [ "@id", box id; "@p", box (FeedbackJson.serialize item) ] ]
        }

    let delete (factory: IDbConnectionFactory) (table: string) (id: string) : Task<bool> =
        task {
            do! ensure factory table
            let! n =
                Ado.executeNonQuery factory (sprintf "DELETE FROM %s WHERE item_id = @id" table) [ "@id", box id ]
            return n > 0
        }

/// Turns persisted in the nao_feedback_turns table (keyed by TurnId).
type AdoTurnStore(factory: IDbConnectionFactory) =
    let table = "nao_feedback_turns"
    interface ITurnStore with
        member _.SaveAsync(turn) = AdoPayload.upsert factory table turn.TurnId turn
        member _.GetAsync(turnId) =
            task {
                let! all = AdoPayload.getAll<TurnRecord> factory table
                return all |> List.tryFind (fun t -> t.TurnId = turnId)
            }
        member _.GetForSessionAsync(sessionId) =
            task {
                let! all = AdoPayload.getAll<TurnRecord> factory table
                return all |> List.filter (fun t -> t.SessionId = sessionId)
            }

/// Feedback persisted in the nao_feedback_entries table (keyed by feedback Id).
type AdoFeedbackStore(factory: IDbConnectionFactory) =
    let table = "nao_feedback_entries"
    interface IFeedbackStore with
        member _.SaveAsync(feedback) = AdoPayload.upsert factory table (feedback.Id.ToString("D")) feedback
        member _.GetForTurnAsync(turnId) =
            task {
                let! all = AdoPayload.getAll<Feedback> factory table
                return all |> List.filter (fun f -> f.TurnId = turnId)
            }
        member _.GetForSessionAsync(sessionId) =
            task {
                let! all = AdoPayload.getAll<Feedback> factory table
                return all |> List.filter (fun f -> f.SessionId = sessionId)
            }
        member _.GetAllAsync() = AdoPayload.getAll<Feedback> factory table

/// Suggestions persisted in the nao_feedback_suggestions table (keyed by Id).
type AdoSuggestionStore(factory: IDbConnectionFactory) =
    let table = "nao_feedback_suggestions"
    interface ISuggestionStore with
        member _.SaveAsync(suggestion) = AdoPayload.upsert factory table (suggestion.Id.ToString("D")) suggestion
        member _.GetAllAsync() = AdoPayload.getAll<Suggestion> factory table
        member _.GetAsync(id) =
            task {
                let! all = AdoPayload.getAll<Suggestion> factory table
                return all |> List.tryFind (fun s -> s.Id = id)
            }
        member _.UpdateAsync(suggestion) =
            task {
                let! all = AdoPayload.getAll<Suggestion> factory table
                if all |> List.exists (fun s -> s.Id = suggestion.Id) then
                    do! AdoPayload.upsert factory table (suggestion.Id.ToString("D")) suggestion
                    return true
                else
                    return false
            }

/// Annotations persisted in the nao_feedback_annotations table (keyed by Id).
type AdoAnnotationStore(factory: IDbConnectionFactory) =
    let table = "nao_feedback_annotations"
    interface IAnnotationStore with
        member _.SaveAsync(annotation) = AdoPayload.upsert factory table (annotation.Id.ToString("D")) annotation
        member _.GetAllAsync() = AdoPayload.getAll<Annotation> factory table
        member _.GetForTargetAsync(kind, targetName) =
            task {
                let! all = AdoPayload.getAll<Annotation> factory table
                return all |> List.filter (fun a -> a.Kind = kind && a.TargetName = targetName)
            }
        member _.UpdateStatusAsync(id, status) =
            task {
                let! all = AdoPayload.getAll<Annotation> factory table
                match all |> List.tryFind (fun a -> a.Id = id) with
                | Some a ->
                    do! AdoPayload.upsert factory table (id.ToString("D")) { a with Status = status }
                    return true
                | None -> return false
            }
        member _.DeleteAsync(id) = AdoPayload.delete factory table (id.ToString("D"))

/// Generated versions persisted in the nao_feedback_versions table (keyed by Id).
type AdoVersionStore(factory: IDbConnectionFactory) =
    let table = "nao_feedback_versions"
    interface IVersionStore with
        member _.SaveAsync(version) = AdoPayload.upsert factory table (version.Id.ToString("D")) version
        member _.GetAllAsync() = AdoPayload.getAll<VersionRecord> factory table
        member _.GetForTargetAsync(kind, targetName) =
            task {
                let! all = AdoPayload.getAll<VersionRecord> factory table
                return all |> List.filter (fun v -> v.Kind = kind && v.TargetName = targetName)
            }
        member _.UpdateStatusAsync(id, status) =
            task {
                let! all = AdoPayload.getAll<VersionRecord> factory table
                match all |> List.tryFind (fun v -> v.Id = id) with
                | Some v ->
                    do! AdoPayload.upsert factory table (id.ToString("D")) { v with Status = status }
                    return true
                | None -> return false
            }
