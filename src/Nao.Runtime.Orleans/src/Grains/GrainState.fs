namespace Nao.Runtime.Orleans.Grains

open System
open Orleans
open Nao.Core
open Nao.Agents

/// Serializable record for a conversation message
[<GenerateSerializer>]
type MessageRecord() =
    [<Id(0u)>] member val Role: string = "" with get, set
    [<Id(1u)>] member val Content: string = "" with get, set

/// Serializable record for a memory entry
[<GenerateSerializer>]
type MemoryRecord() =
    [<Id(0u)>] member val Key: string = "" with get, set
    [<Id(1u)>] member val Value: string = "" with get, set
    [<Id(2u)>] member val Timestamp: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(3u)>] member val Tags: ResizeArray<string> = ResizeArray() with get, set

/// Mapping between Orleans serializable records and domain types
module GrainStateMapping =

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

    let fromMemoryEntry (entry: MemoryEntry) : MemoryEntry =
        { Key = entry.Key
          Value = entry.Value
          Timestamp = entry.Timestamp
          Tags = entry.Tags }
