namespace Nao.Agents

open System

/// Interface for agent logging
type IAgentLogger =
    abstract member Log: LogLevel -> string -> unit
    abstract member LogWith: LogLevel -> string -> Map<string, obj> -> unit

/// Default console logger implementation
module AgentLogger =

    /// Create a logger that writes to console
    let console (source: string) : IAgentLogger =
        { new IAgentLogger with
            member _.Log level message =
                let ts = DateTimeOffset.UtcNow.ToString("o")
                printfn "[%s] [%A] [%s] %s" ts level source message

            member _.LogWith level message data =
                let ts = DateTimeOffset.UtcNow.ToString("o")
                let dataStr =
                    data
                    |> Map.toList
                    |> List.map (fun (k, v) -> sprintf "%s=%O" k v)
                    |> String.concat ", "
                printfn "[%s] [%A] [%s] %s {%s}" ts level source message dataStr }

    /// Create a silent logger (no output)
    let silent : IAgentLogger =
        { new IAgentLogger with
            member _.Log _ _ = ()
            member _.LogWith _ _ _ = () }

    /// Create a logger that collects entries into a mutable list (for testing/debugging)
    let collect (entries: ResizeArray<LogEntry>) (source: string) : IAgentLogger =
        { new IAgentLogger with
            member _.Log level message =
                entries.Add
                    { Timestamp = DateTimeOffset.UtcNow
                      Level = level
                      Source = source
                      Message = message
                      Data = Map.empty }

            member _.LogWith level message data =
                entries.Add
                    { Timestamp = DateTimeOffset.UtcNow
                      Level = level
                      Source = source
                      Message = message
                      Data = data } }
