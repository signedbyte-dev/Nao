namespace Nao.Assistant

open System
open System.IO
open System.Net.WebSockets
open System.Net.Sockets
open System.Data.Common
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Persistence
open Nao.Feedback
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

module AssistantTools =

    let private workDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nao-workspace")

    let private resolvePath (input: string) =
        let name = input.Trim().Replace("\\", "/").TrimStart('/')
        Path.GetFullPath(Path.Combine(workDir, name))

    let ensureWorkDir () =
        Directory.CreateDirectory(workDir) |> ignore
        workDir

    let createFolder: Tool =
        Tool.Create("create_folder", "Create a new folder. Input: relative folder path.",
            fun input -> task {
                let path = resolvePath input
                Directory.CreateDirectory(path) |> ignore
                return sprintf """{"created":"%s","exists":true}""" (path.Replace("\\", "/"))
            })

    let writeFile: Tool =
        Tool.Create("write_file", "Write content to a file. Input format: 'relative/path|content'.",
            fun input -> task {
                let parts = input.Split('|', 2)
                if parts.Length < 2 then return """{"error":"Expected 'path|content'"}"""
                else
                    let path = resolvePath parts.[0]
                    let dir = Path.GetDirectoryName(path)
                    if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
                    do! File.WriteAllTextAsync(path, parts.[1])
                    return sprintf """{"written":"%s","bytes":%d}""" (path.Replace("\\", "/")) parts.[1].Length
            })

    let readFile: Tool =
        Tool.Create("read_file", "Read content from a file. Input: relative file path.",
            fun input -> task {
                let path = resolvePath input
                if File.Exists(path) then
                    let! content = File.ReadAllTextAsync(path)
                    return content
                else
                    return sprintf """{"error":"File not found: %s"}""" input
            })

    let listFolder: Tool =
        Tool.Create("list_folder", "List directory contents. Input: relative path (empty for root).",
            fun input -> task {
                let path = if String.IsNullOrWhiteSpace(input) then workDir else resolvePath input
                if not (Directory.Exists(path)) then
                    return sprintf """{"error":"Directory not found: %s"}""" input
                else
                    let entries =
                        Directory.GetFileSystemEntries(path)
                        |> Array.map (fun e ->
                            let name = Path.GetFileName(e)
                            let isDir = Directory.Exists(e)
                            sprintf """{"name":"%s","type":"%s"}""" name (if isDir then "dir" else "file"))
                        |> String.concat ","
                    return sprintf """{"path":"%s","entries":[%s]}""" (path.Replace("\\", "/")) entries
            })

    let delete: Tool =
        Tool.Create("delete", "Delete a file or folder. Input: relative path.",
            fun input -> task {
                let path = resolvePath input
                if File.Exists(path) then
                    File.Delete(path)
                    return sprintf """{"deleted":"%s","type":"file"}""" input
                elif Directory.Exists(path) then
                    Directory.Delete(path, true)
                    return sprintf """{"deleted":"%s","type":"dir"}""" input
                else
                    return sprintf """{"error":"Not found: %s"}""" input
            })

    let dateTime: Tool =
        Tool.Create("get_datetime", "Get the current date and time.",
            fun _ -> task {
                return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
            })

    let calculator: Tool =
        Tool.Create("calculator", "Evaluate a simple math expression. Input: expression like '2 + 3'.",
            fun input -> task {
                try
                    let parts = input.Trim().Split(' ')
                    if parts.Length = 3 then
                        let a = Double.Parse(parts.[0])
                        let b = Double.Parse(parts.[2])
                        let result =
                            match parts.[1] with
                            | "+" -> a + b | "-" -> a - b
                            | "*" -> a * b | "/" -> if b <> 0.0 then a / b else Double.NaN
                            | _ -> Double.NaN
                        return sprintf """{"result":%g}""" result
                    else
                        return """{"error":"Expected format: 'a op b'"}"""
                with ex ->
                    return sprintf """{"error":"%s"}""" ex.Message
            })

    /// Shared HTTP client for the web tools (connection pooling, sane timeout).
    let private httpClient =
        let c = new System.Net.Http.HttpClient()
        c.Timeout <- TimeSpan.FromSeconds(20.0)
        c.DefaultRequestHeaders.Add("User-Agent", "Nao-Assistant/1.0")
        c

    let private truncate (max: int) (s: string) =
        if s.Length > max then s.Substring(0, max) + "\n...(truncated)" else s

    let httpRequest: Tool =
        Tool.Create("http_request",
            "Make an HTTP request to any URL. Input: 'METHOD URL' or 'METHOD URL|body' (METHOD defaults to GET). Returns the status code and response body.",
            fun input -> task {
                try
                    let trimmed = input.Trim()
                    let reqPart, body =
                        match trimmed.IndexOf('|') with
                        | -1 -> trimmed, None
                        | i -> trimmed.Substring(0, i).Trim(), Some (trimmed.Substring(i + 1))
                    let parts = reqPart.Split([| ' ' |], 2, StringSplitOptions.RemoveEmptyEntries)
                    let methodStr, url =
                        if parts.Length = 2 && not (parts.[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        then parts.[0].ToUpperInvariant(), parts.[1].Trim()
                        else "GET", reqPart
                    use req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod(methodStr), url)
                    match body with
                    | Some b -> req.Content <- new System.Net.Http.StringContent(b, Encoding.UTF8, "application/json")
                    | None -> ()
                    let! resp = httpClient.SendAsync(req)
                    let! content = resp.Content.ReadAsStringAsync()
                    return sprintf """{"status":%d,"body":%s}""" (int resp.StatusCode) (JsonSerializer.Serialize(truncate 8000 content))
                with ex ->
                    return sprintf """{"error":%s}""" (JsonSerializer.Serialize(ex.Message))
            })

    let webFetch: Tool =
        Tool.Create("web_fetch",
            "Fetch a web page and return its readable text content (HTML tags stripped). Input: a URL.",
            fun input -> task {
                try
                    let url = input.Trim()
                    let! html = httpClient.GetStringAsync(url)
                    let noScript = Regex.Replace(html, "(?is)<(script|style)[^>]*>.*?</\\1>", " ")
                    let noTags = Regex.Replace(noScript, "(?s)<[^>]+>", " ")
                    let decoded = System.Net.WebUtility.HtmlDecode(noTags)
                    let collapsed = Regex.Replace(decoded, "\\s+", " ").Trim()
                    return truncate 8000 collapsed
                with ex ->
                    return sprintf """{"error":%s}""" (JsonSerializer.Serialize(ex.Message))
            })

    let searchFiles: Tool =
        Tool.Create("search_files",
            "Search workspace files for a regex pattern. Input: 'pattern' or 'pattern|subdir'. Returns up to 200 'relative/path:line: text' matches.",
            fun input -> task {
                try
                    let trimmed = input.Trim()
                    let pattern, sub =
                        match trimmed.IndexOf('|') with
                        | -1 -> trimmed, ""
                        | i -> trimmed.Substring(0, i).Trim(), trimmed.Substring(i + 1).Trim()
                    let root = if String.IsNullOrWhiteSpace sub then workDir else resolvePath sub
                    if not (Directory.Exists root) then return sprintf """{"error":"Directory not found: %s"}""" sub
                    else
                        let regex = Regex(pattern, RegexOptions.IgnoreCase)
                        let matches = ResizeArray<string>()
                        for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
                            if matches.Count < 200 then
                                try
                                    let lines = File.ReadAllLines(file)
                                    lines |> Array.iteri (fun i line ->
                                        if matches.Count < 200 && regex.IsMatch(line) then
                                            let rel = Path.GetRelativePath(workDir, file).Replace("\\", "/")
                                            matches.Add(sprintf "%s:%d: %s" rel (i + 1) (line.Trim())))
                                with _ -> ()
                        return (if matches.Count = 0 then "(no matches)" else String.Join("\n", matches))
                with ex ->
                    return sprintf """{"error":%s}""" (JsonSerializer.Serialize(ex.Message))
            })

    let findFiles: Tool =
        Tool.Create("find_files",
            "Find files in the workspace by glob pattern (supports *, ?, and ** for any depth). Input: e.g. '**/*.json'.",
            fun input -> task {
                try
                    let glob = input.Trim().Replace("\\", "/").TrimStart('/')
                    let escaped =
                        Regex.Escape(glob)
                            .Replace("\\*\\*/", "(.*/)?")
                            .Replace("\\*\\*", ".*")
                            .Replace("\\*", "[^/]*")
                            .Replace("\\?", "[^/]")
                    let regex = Regex("^" + escaped + "$", RegexOptions.IgnoreCase)
                    if not (Directory.Exists workDir) then return "(workspace empty)"
                    else
                        let results =
                            Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
                            |> Seq.map (fun f -> Path.GetRelativePath(workDir, f).Replace("\\", "/"))
                            |> Seq.filter regex.IsMatch
                            |> Seq.truncate 500
                            |> List.ofSeq
                        return (if results.IsEmpty then "(no matches)" else String.Join("\n", results))
                with ex ->
                    return sprintf """{"error":%s}""" (JsonSerializer.Serialize(ex.Message))
            })

    /// Map a file extension to the IANA media type understood by the document registry.
    let private mediaTypeForExt (ext: string) =
        match ext.ToLowerInvariant() with
        | ".md" | ".markdown" -> Some Nao.Documents.Markdown.MediaType
        | ".txt" | ".text" | "" -> Some Nao.Documents.PlainText.MediaType
        | _ -> None

    let private documentRegistry = Nao.Documents.Markdown.defaultRegistry ()

    let convertDocument: Tool =
        Tool.Create("convert_document",
            "Convert a workspace document from one format to another via the unified document model. "
            + "Input format: 'source/relative/path|target/relative/path'. Formats are inferred from the "
            + "file extensions. Supported: .md/.markdown and .txt. Extracted media is saved next to the target.",
            fun input -> task {
                try
                    let parts = input.Split('|', 2)
                    if parts.Length < 2 then
                        return """{"error":"Expected 'sourcePath|targetPath'"}"""
                    else
                        let sourcePath = resolvePath parts.[0]
                        let targetPath = resolvePath parts.[1]
                        if not (File.Exists sourcePath) then
                            return sprintf """{"error":"Source not found: %s"}""" (parts.[0].Trim())
                        else
                            let srcMt = mediaTypeForExt (Path.GetExtension sourcePath)
                            let tgtMt = mediaTypeForExt (Path.GetExtension targetPath)
                            match srcMt, tgtMt with
                            | None, _ ->
                                return sprintf """{"error":"Unsupported source format: %s"}""" (Path.GetExtension sourcePath)
                            | _, None ->
                                return sprintf """{"error":"Unsupported target format: %s"}""" (Path.GetExtension targetPath)
                            | Some src, Some tgt ->
                                let dir = Path.GetDirectoryName(targetPath)
                                if not (Directory.Exists dir) then Directory.CreateDirectory(dir) |> ignore
                                Nao.Documents.Converter.convertFile documentRegistry src tgt sourcePath targetPath
                                let bytes = (FileInfo targetPath).Length
                                return sprintf """{"converted":"%s","from":"%s","to":"%s","bytes":%d}"""
                                        (targetPath.Replace("\\", "/")) src tgt bytes
                with ex ->
                    return sprintf """{"error":%s}""" (JsonSerializer.Serialize(ex.Message))
            })

    let allTools = [ createFolder; writeFile; readFile; listFolder; delete; dateTime; calculator
                     httpRequest; webFetch; searchFiles; findFiles; convertDocument ]


