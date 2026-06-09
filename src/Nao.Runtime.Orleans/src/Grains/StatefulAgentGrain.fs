namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Nao.Core
open Nao.Agents

/// Persistent state for an agent grain, stored via Orleans grain persistence
[<GenerateSerializer>]
type AgentGrainState() =
    [<Id(0u)>]
    member val ConversationHistory: ResizeArray<MessageRecord> = ResizeArray() with get, set

    [<Id(1u)>]
    member val Memories: ResizeArray<MemoryRecord> = ResizeArray() with get, set

/// Serializable record for a conversation message
and [<GenerateSerializer>] MessageRecord() =
    [<Id(0u)>] member val Role: string = "" with get, set
    [<Id(1u)>] member val Content: string = "" with get, set

/// Serializable record for a memory entry
and [<GenerateSerializer>] MemoryRecord() =
    [<Id(0u)>] member val Key: string = "" with get, set
    [<Id(1u)>] member val Value: string = "" with get, set
    [<Id(2u)>] member val Timestamp: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(3u)>] member val Tags: ResizeArray<string> = ResizeArray() with get, set

module AgentGrainStateMapping =

    let toMessage (record: MessageRecord) : Message =
        let role =
            match record.Role with
            | "System" -> System
            | "Assistant" -> Assistant
            | _ -> User
        { Role = role; Content = record.Content }

    let fromMessage (msg: Message) : MessageRecord =
        let role =
            match msg.Role with
            | System -> "System"
            | User -> "User"
            | Assistant -> "Assistant"
        let r = MessageRecord()
        r.Role <- role
        r.Content <- msg.Content
        r

    let toMemoryEntry (record: MemoryRecord) : MemoryEntry =
        { Key = record.Key
          Value = record.Value
          Timestamp = record.Timestamp
          Tags = record.Tags |> Seq.toList }

    let fromMemoryEntry (entry: MemoryEntry) : MemoryRecord =
        let r = MemoryRecord()
        r.Key <- entry.Key
        r.Value <- entry.Value
        r.Timestamp <- entry.Timestamp
        r.Tags <- ResizeArray(entry.Tags)
        r

/// Extended grain interface with memory management capabilities
type IStatefulAgentGrain =
    inherit IGrainWithStringKey

    /// Process an input message and return the agent's response
    abstract member ProcessAsync: input: string -> Task<string>

    /// Send a message to this agent from another agent
    abstract member ReceiveMessageAsync: fromAgent: string -> message: string -> Task<string option>

    /// Get the current agent name/identity
    abstract member GetAgentIdAsync: unit -> Task<string>

    /// Get the current conversation history
    abstract member GetHistoryAsync: unit -> Task<Message list>

    /// Clear conversation history
    abstract member ClearHistoryAsync: unit -> Task<unit>

    /// Save a memory
    abstract member SaveMemoryAsync: key: string -> value: string -> Task<unit>

    /// Recall memories by key
    abstract member RecallMemoryAsync: key: string -> Task<MemoryEntry list>

    /// Clear all memories
    abstract member ClearMemoriesAsync: unit -> Task<unit>

/// Base grain with Orleans persistent state for conversation and memory
[<AbstractClass>]
type StatefulAgentGrainBase
    (
        [<PersistentState("agentState", "agentStore")>] persistentState: IPersistentState<AgentGrainState>
    ) =
    inherit Grain()

    abstract member Agent: IAgent
    abstract member WindowStrategy: WindowStrategy option

    /// Load conversation from persisted state into the agent
    member this.RestoreState() =
        let messages =
            persistentState.State.ConversationHistory
            |> Seq.map AgentGrainStateMapping.toMessage
            |> Seq.toList
        // The agent's state is mutable, so the subclass handles restoration
        messages

    /// Persist the current conversation
    member this.PersistConversationAsync(conversation: Conversation) : Task =
        persistentState.State.ConversationHistory.Clear()
        for msg in conversation do
            persistentState.State.ConversationHistory.Add(AgentGrainStateMapping.fromMessage msg)
        persistentState.WriteStateAsync()

    interface IStatefulAgentGrain with
        member this.ProcessAsync(input: string) : Task<string> =
            task {
                let! result = this.Agent.RunAsync input

                // Apply windowing before persisting
                let conversation =
                    match this.WindowStrategy with
                    | Some strategy -> ConversationWindow.apply strategy this.Agent.State.Conversation
                    | None -> this.Agent.State.Conversation

                do! this.PersistConversationAsync(conversation)
                return result
            }

        member this.ReceiveMessageAsync (fromAgent: string) (message: string) : Task<string option> =
            task {
                let senderId = { Name = fromAgent; Description = "" }
                let msg = AgentMessage.broadcast senderId message
                let! reply = this.Agent.HandleMessageAsync msg
                do! this.PersistConversationAsync(this.Agent.State.Conversation)
                return reply |> Option.map (fun m -> m.Content)
            }

        member this.GetAgentIdAsync() : Task<string> =
            Task.FromResult(this.Agent.Id.Name)

        member _.GetHistoryAsync() : Task<Message list> =
            persistentState.State.ConversationHistory
            |> Seq.map AgentGrainStateMapping.toMessage
            |> Seq.toList
            |> Task.FromResult

        member this.ClearHistoryAsync() : Task<unit> =
            task {
                persistentState.State.ConversationHistory.Clear()
                do! persistentState.WriteStateAsync()
            }

        member this.SaveMemoryAsync (key: string) (value: string) : Task<unit> =
            task {
                let record = MemoryRecord()
                record.Key <- key
                record.Value <- value
                record.Timestamp <- DateTimeOffset.UtcNow
                // Replace existing entry with same key
                let existing = persistentState.State.Memories |> Seq.tryFindIndex (fun m -> m.Key = key)
                match existing with
                | Some idx -> persistentState.State.Memories.[idx] <- record
                | None -> persistentState.State.Memories.Add(record)
                do! persistentState.WriteStateAsync()
            }

        member _.RecallMemoryAsync (key: string) : Task<MemoryEntry list> =
            persistentState.State.Memories
            |> Seq.filter (fun m -> m.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
            |> Seq.map AgentGrainStateMapping.toMemoryEntry
            |> Seq.toList
            |> Task.FromResult

        member this.ClearMemoriesAsync() : Task<unit> =
            task {
                persistentState.State.Memories.Clear()
                do! persistentState.WriteStateAsync()
            }
