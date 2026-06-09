namespace Nao.Providers

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Nao.Core

/// LLM provider that connects to an Ollama server via its OpenAI-compatible API.
/// Ollama exposes /v1/chat/completions for chat-style completions.
type OllamaProvider(config: OllamaConfig) =
    let client = new HttpClient(BaseAddress = Uri(config.BaseUrl))

    let roleToString (role: Role) =
        match role with
        | System -> "system"
        | User -> "user"
        | Assistant -> "assistant"

    let buildRequestBody (conversation: Conversation) (options: CompletionOptions) =
        use stream = new System.IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("model", config.Model)

        writer.WriteStartArray("messages")
        for m in conversation do
            writer.WriteStartObject()
            writer.WriteString("role", roleToString m.Role)
            writer.WriteString("content", m.Content)
            writer.WriteEndObject()
        writer.WriteEndArray()

        writer.WriteNumber("temperature", options.Temperature)
        writer.WriteBoolean("stream", false)

        match options.MaxTokens with
        | Some t -> writer.WriteNumber("max_tokens", t)
        | None -> ()

        match options.StopSequences with
        | [] -> ()
        | seqs ->
            writer.WriteStartArray("stop")
            for s in seqs do
                writer.WriteStringValue(s)
            writer.WriteEndArray()

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let parseResponse (json: string) : CompletionResult =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let content =
                match root.TryGetProperty("choices") with
                | true, choices when choices.GetArrayLength() > 0 ->
                    let firstChoice = choices.[0]
                    match firstChoice.TryGetProperty("message") with
                    | true, message ->
                        match message.TryGetProperty("content") with
                        | true, c -> c.GetString()
                        | _ -> ""
                    | _ -> ""
                | _ -> ""

            let finishReason =
                match root.TryGetProperty("choices") with
                | true, choices when choices.GetArrayLength() > 0 ->
                    match choices.[0].TryGetProperty("finish_reason") with
                    | true, fr when fr.ValueKind = JsonValueKind.String ->
                        let r = fr.GetString()
                        if String.IsNullOrEmpty(r) then "stop" else r
                    | _ -> "stop"
                | _ -> "stop"

            let totalTokens =
                match root.TryGetProperty("usage") with
                | true, usage ->
                    match usage.TryGetProperty("total_tokens") with
                    | true, t when t.ValueKind = JsonValueKind.Number -> Some (t.GetInt32())
                    | _ -> None
                | _ -> None

            { Content = content
              FinishReason = finishReason
              TokensUsed = totalTokens }
        with ex ->
            { Content = sprintf "Parse error: %s" ex.Message
              FinishReason = "error"
              TokensUsed = None }

    interface ILlmProvider with
        member _.Name = sprintf "Ollama(%s)" config.Model

        member _.CompleteAsync (conversation: Conversation) (options: CompletionOptions) : Task<CompletionResult> =
            task {
                let body = buildRequestBody conversation options
                let content = new StringContent(body, Encoding.UTF8, "application/json")

                let! response = client.PostAsync("/v1/chat/completions", content)
                let! responseBody = response.Content.ReadAsStringAsync()

                if not response.IsSuccessStatusCode then
                    return
                        { Content = sprintf "Error: %d - %s" (int response.StatusCode) responseBody
                          FinishReason = "error"
                          TokensUsed = None }
                else
                    return parseResponse responseBody
            }

    interface IDisposable with
        member _.Dispose() = client.Dispose()
