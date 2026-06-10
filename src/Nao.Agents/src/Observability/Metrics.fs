namespace Nao.Agents

open System
open System.Threading.Tasks

/// Cost model for LLM provider pricing
type CostModel =
    { /// Provider name
      Provider: string
      /// Model name
      Model: string
      /// Cost per 1K input tokens in USD
      InputCostPer1K: decimal
      /// Cost per 1K output tokens in USD
      OutputCostPer1K: decimal }

/// A single metrics data point
type MetricPoint =
    { /// Metric name
      Name: string
      /// Metric value
      Value: float
      /// When recorded
      Timestamp: DateTimeOffset
      /// Dimension labels
      Labels: Map<string, string> }

/// Aggregated metrics for an agent execution
type ExecutionMetrics =
    { /// Core resource usage (shared with Environment layer)
      Usage: ResourceUsage
      /// Total LLM calls made
      TotalLlmCalls: int
      /// Total input tokens consumed
      TotalInputTokens: int
      /// Total output tokens generated
      TotalOutputTokens: int
      /// Total estimated cost in USD
      TotalCostUsd: decimal
      /// Total tool invocations
      TotalToolCalls: int
      /// Average latency per LLM call in milliseconds
      AvgLatencyMs: float
      /// P95 latency in milliseconds
      P95LatencyMs: float
      /// Total execution time
      TotalDuration: TimeSpan
      /// Time spent waiting for LLM responses
      LlmWaitTime: TimeSpan
      /// Time spent in tool execution
      ToolExecutionTime: TimeSpan }

    static member Zero =
        { Usage = ResourceUsage.Zero
          TotalLlmCalls = 0
          TotalInputTokens = 0
          TotalOutputTokens = 0
          TotalCostUsd = 0m
          TotalToolCalls = 0
          AvgLatencyMs = 0.0
          P95LatencyMs = 0.0
          TotalDuration = TimeSpan.Zero
          LlmWaitTime = TimeSpan.Zero
          ToolExecutionTime = TimeSpan.Zero }

    /// Create from ResourceUsage (bridge from Environment layer)
    static member FromUsage (usage: ResourceUsage) =
        { ExecutionMetrics.Zero with
            Usage = usage
            TotalLlmCalls = usage.LlmCalls
            TotalToolCalls = usage.ToolCalls
            TotalCostUsd = usage.EstimatedCostUsd
            TotalDuration = usage.ElapsedTime }

/// Interface for metrics collection
type IMetricsCollector =
    /// Record an LLM call with token counts and latency
    abstract member RecordLlmCall: inputTokens: int -> outputTokens: int -> latencyMs: int64 -> unit
    /// Record a tool invocation with duration
    abstract member RecordToolCall: toolName: string -> durationMs: int64 -> success: bool -> unit
    /// Record a custom metric point
    abstract member RecordMetric: MetricPoint -> unit
    /// Get aggregated metrics
    abstract member GetMetrics: unit -> ExecutionMetrics
    /// Calculate cost using a cost model
    abstract member EstimateCost: CostModel -> decimal

/// In-memory metrics collector
type InMemoryMetricsCollector() =
    let llmLatencies = ResizeArray<int64>()
    let mutable inputTokens = 0
    let mutable outputTokens = 0
    let mutable llmCalls = 0
    let mutable toolCalls = 0
    let mutable llmWaitMs = 0L
    let mutable toolExecMs = 0L
    let startTime = DateTimeOffset.UtcNow

    interface IMetricsCollector with
        member _.RecordLlmCall (inTokens: int) (outTokens: int) (latencyMs: int64) =
            llmCalls <- llmCalls + 1
            inputTokens <- inputTokens + inTokens
            outputTokens <- outputTokens + outTokens
            llmLatencies.Add(latencyMs)
            llmWaitMs <- llmWaitMs + latencyMs

        member _.RecordToolCall (_toolName: string) (durationMs: int64) (_success: bool) =
            toolCalls <- toolCalls + 1
            toolExecMs <- toolExecMs + durationMs

        member _.RecordMetric(_point: MetricPoint) = ()

        member _.GetMetrics() =
            let sortedLatencies = llmLatencies |> Seq.sort |> Seq.toArray
            let avgLatency =
                if sortedLatencies.Length > 0 then
                    sortedLatencies |> Array.averageBy float
                else 0.0
            let p95Latency =
                if sortedLatencies.Length > 0 then
                    let idx = int (float sortedLatencies.Length * 0.95)
                    float sortedLatencies.[min idx (sortedLatencies.Length - 1)]
                else 0.0

            let duration = DateTimeOffset.UtcNow - startTime
            let usage : ResourceUsage =
                { LlmCalls = llmCalls
                  TotalTokens = inputTokens + outputTokens
                  ToolCalls = toolCalls
                  EstimatedCostUsd = 0m
                  ElapsedTime = duration }

            { Usage = usage
              TotalLlmCalls = llmCalls
              TotalInputTokens = inputTokens
              TotalOutputTokens = outputTokens
              TotalCostUsd = 0m
              TotalToolCalls = toolCalls
              AvgLatencyMs = avgLatency
              P95LatencyMs = p95Latency
              TotalDuration = duration
              LlmWaitTime = TimeSpan.FromMilliseconds(float llmWaitMs)
              ToolExecutionTime = TimeSpan.FromMilliseconds(float toolExecMs) }

        member _.EstimateCost(model: CostModel) =
            let inCost = decimal inputTokens / 1000m * model.InputCostPer1K
            let outCost = decimal outputTokens / 1000m * model.OutputCostPer1K
            inCost + outCost

module MetricsCollector =
    let inMemory () : IMetricsCollector = InMemoryMetricsCollector() :> IMetricsCollector

    /// Well-known cost models
    let gpt4o = { Provider = "OpenAI"; Model = "gpt-4o"; InputCostPer1K = 0.0025m; OutputCostPer1K = 0.01m }
    let gpt4oMini = { Provider = "OpenAI"; Model = "gpt-4o-mini"; InputCostPer1K = 0.00015m; OutputCostPer1K = 0.0006m }
    let claude35Sonnet = { Provider = "Anthropic"; Model = "claude-3.5-sonnet"; InputCostPer1K = 0.003m; OutputCostPer1K = 0.015m }
    let claude4Sonnet = { Provider = "Anthropic"; Model = "claude-sonnet-4"; InputCostPer1K = 0.003m; OutputCostPer1K = 0.015m }
