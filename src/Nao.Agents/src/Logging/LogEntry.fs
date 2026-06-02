namespace Nao.Agents

open System

/// A single log entry produced during agent execution
type LogEntry =
    { Timestamp: DateTimeOffset
      Level: LogLevel
      Source: string
      Message: string
      Data: Map<string, obj> }
