namespace Nao.Demo

open System
open System.IO
open System.Threading.Tasks
open Nao.Core
open Nao.Agents

/// File-system tools with real I/O, verify, and revert support.
/// Demonstrates: ContentMeta, Tool.Verify, Tool.Revert, and the execution journal.
module FileSystemTools =

    let private workDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nao-demo-workspace")

    let private resolvePath (input: string) =
        let name = input.Trim().Replace("\\", "/").TrimStart('/')
        Path.GetFullPath(Path.Combine(workDir, name))

    /// Ensure the workspace root exists
    let ensureWorkDir () =
        Directory.CreateDirectory(workDir) |> ignore
        workDir

    /// Create a folder. Input: relative path.
    let createFolder: Tool =
        { Tool.Create("create_folder", "Create a new folder. Input: relative folder path.",
            fun input -> task {
                let path = resolvePath input
                Directory.CreateDirectory(path) |> ignore
                return sprintf """{"created":"%s","exists":true}""" (path.Replace("\\", "/"))
            }) with
            OutputContentType = ContentMeta.Json
            Verify = Some (fun input _output -> task {
                let path = resolvePath input
                if Directory.Exists(path) then return Ok ()
                else return Error (sprintf "Directory does not exist: %s" path)
            })
            Revert = Some (fun ctx -> task {
                let path = resolvePath ctx.Input
                if Directory.Exists(path) then
                    Directory.Delete(path, true)
                return Ok ()
            }) }

    /// Write a file. Input: "relative/path|content"
    let writeFile: Tool =
        { Tool.Create("write_file", "Write content to a file. Input format: 'relative/path|content'.",
            fun input -> task {
                let parts = input.Split('|', 2)
                if parts.Length < 2 then return """{"error":"Expected 'path|content'"}"""
                else
                    let path = resolvePath parts.[0]
                    let dir = Path.GetDirectoryName(path)
                    if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
                    do! File.WriteAllTextAsync(path, parts.[1])
                    return sprintf """{"written":"%s","bytes":%d}""" (path.Replace("\\", "/")) parts.[1].Length
            }) with
            OutputContentType = ContentMeta.Json
            Verify = Some (fun input _output -> task {
                let parts = input.Split('|', 2)
                if parts.Length < 2 then return Error "Invalid input"
                else
                    let path = resolvePath parts.[0]
                    if File.Exists(path) then
                        let! content = File.ReadAllTextAsync(path)
                        if content = parts.[1] then return Ok ()
                        else return Error "Content mismatch"
                    else return Error "File does not exist"
            })
            Revert = Some (fun ctx -> task {
                let parts = ctx.Input.Split('|', 2)
                if parts.Length >= 1 then
                    let path = resolvePath parts.[0]
                    if File.Exists(path) then File.Delete(path)
                return Ok ()
            }) }

    /// Read a file. Input: relative path.
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

    /// List folder contents. Input: relative folder path (empty = workspace root).
    let listFolder: Tool =
        { Tool.Create("list_folder", "List directory contents. Input: relative path (empty for root).",
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
            }) with
            OutputContentType = ContentMeta.Json }

    /// Delete a file or folder. Input: relative path.
    let delete: Tool =
        { Tool.Create("delete", "Delete a file or folder. Input: relative path.",
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
            }) with
            OutputContentType = ContentMeta.Json }

    let allTools = [ createFolder; writeFile; readFile; listFolder; delete ]


/// System/utility tools for general queries.
module SystemTools =

    /// Get the current date and time.
    let dateTime: Tool =
        Tool.Create("get_datetime", "Get the current date and time.",
            fun _input -> task {
                return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
            })

    /// Simple math evaluator.
    let calculator: Tool =
        Tool.Create("calculator", "Evaluate a simple math expression. Input: expression like '2 + 3'.",
            fun input -> task {
                try
                    // Simple tokenized evaluation for basic ops
                    let parts = input.Trim().Split(' ')
                    if parts.Length = 3 then
                        let a = Double.Parse(parts.[0])
                        let b = Double.Parse(parts.[2])
                        let result =
                            match parts.[1] with
                            | "+" -> a + b
                            | "-" -> a - b
                            | "*" -> a * b
                            | "/" when b <> 0.0 -> a / b
                            | "/" -> Double.NaN
                            | "%" when b <> 0.0 -> a % b
                            | _ -> Double.NaN
                        return sprintf "%g" result
                    else
                        return sprintf "Cannot evaluate: %s" input
                with _ ->
                    return sprintf "Cannot evaluate: %s" input
            })

    let allTools = [ dateTime; calculator ]
