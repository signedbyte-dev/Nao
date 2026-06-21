namespace Nao.Documents

open System
open System.IO
open System.Text

/// Reader and writer for `text/markdown` (CommonMark-ish subset). Markdown is the
/// canonical *fluid* format, so it exercises headings, lists, code, quotes, tables,
/// emphasis, links and images of the unified model.
module Markdown =

    [<Literal>]
    let MediaType = "text/markdown"

    // --- Writer: Document -> Markdown ---------------------------------------------

    let rec private renderInline (i: Inline) : string =
        match i with
        | Run(text, style) ->
            let bold = style.Weight = Some Bold || style.Weight = Some Black
            let italic = style.Style = Some Italic || style.Style = Some Oblique
            let strike = style.Decorations |> List.contains LineThrough
            let mutable s = text
            if strike then s <- "~~" + s + "~~"
            if bold then s <- "**" + s + "**"
            if italic then s <- "*" + s + "*"
            s
        | LineBreak -> "  \n"
        | Link(children, href) ->
            let inner = children |> List.map renderInline |> String.concat ""
            sprintf "[%s](%s)" inner href
        | InlineImage(ResourceId id, alt, _) ->
            sprintf "![%s](%s)" (defaultArg alt "") id
        | InlineCode code -> "`" + code + "`"
        | InlineRaw(_, data) -> data

    let private renderInlines (inlines: Inline list) =
        inlines |> List.map renderInline |> String.concat ""

    let private resourceRef (doc: Document) (id: ResourceId) =
        match Document.tryResource id doc with
        | Some r -> r.LocalPath |> Option.orElse r.Uri |> Option.defaultValue id.Value
        | None -> id.Value

    let rec private renderBlock (doc: Document) (sb: StringBuilder) (block: Block) =
        match block with
        | Heading(level, inlines) ->
            let hashes = String('#', max 1 (min 6 level))
            sb.Append(hashes).Append(' ').AppendLine(renderInlines inlines).AppendLine() |> ignore
        | Paragraph(inlines, _) ->
            sb.AppendLine(renderInlines inlines).AppendLine() |> ignore
        | ListBlock spec ->
            spec.Items
            |> List.iteri (fun idx item ->
                let marker =
                    if spec.Ordered then sprintf "%d. " (idx + defaultArg spec.Start 1)
                    else "- "
                match item.Content with
                | Paragraph(inlines, _) :: _ ->
                    sb.Append(marker).AppendLine(renderInlines inlines) |> ignore
                | _ ->
                    sb.Append(marker).AppendLine("") |> ignore)
            sb.AppendLine() |> ignore
        | CodeBlock(lang, code) ->
            sb.Append("```").AppendLine(defaultArg lang "") |> ignore
            sb.AppendLine(code) |> ignore
            sb.AppendLine("```").AppendLine() |> ignore
        | Quote blocks ->
            let inner = StringBuilder()
            blocks |> List.iter (renderBlock doc inner)
            inner.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n')
            |> Array.iter (fun line -> sb.Append("> ").AppendLine(line) |> ignore)
            sb.AppendLine() |> ignore
        | Media spec ->
            let path = resourceRef doc spec.Resource
            sb.AppendLine(sprintf "![%s](%s)" (defaultArg spec.Alt "") path) |> ignore
            if not spec.Caption.IsEmpty then
                sb.Append('*').Append(renderInlines spec.Caption).AppendLine("*") |> ignore
            sb.AppendLine() |> ignore
        | Table spec ->
            let renderRow (row: TableRow) =
                "| "
                + (row.Cells
                   |> List.map (fun c ->
                       c.Content
                       |> List.choose (function Paragraph(i, _) -> Some(renderInlines i) | _ -> None)
                       |> String.concat " ")
                   |> String.concat " | ")
                + " |"
            let columnCount =
                match spec.Header with
                | Some h -> h.Cells.Length
                | None -> spec.Rows |> List.tryHead |> Option.map (fun r -> r.Cells.Length) |> Option.defaultValue 0
            spec.Header
            |> Option.iter (fun h ->
                sb.AppendLine(renderRow h) |> ignore
                sb.AppendLine("|" + String.replicate columnCount " --- |") |> ignore)
            spec.Rows |> List.iter (fun r -> sb.AppendLine(renderRow r) |> ignore)
            sb.AppendLine() |> ignore
        | Container spec ->
            spec.Children |> List.iter (fun c -> renderBlock doc sb c.Content)
        | ThematicBreak ->
            sb.AppendLine("---").AppendLine() |> ignore
        | BlockRaw(_, data) ->
            sb.AppendLine(data).AppendLine() |> ignore

    /// Writes a `Document` as Markdown.
    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]
            member _.Write(doc, output, _ctx) =
                let sb = StringBuilder()
                doc.Metadata.Title |> Option.iter (fun t -> sb.Append("# ").AppendLine(t).AppendLine() |> ignore)
                Document.toBlocks doc |> List.iter (renderBlock doc sb)
                use w = new StreamWriter(output, UTF8Encoding(false), 1024, leaveOpen = true)
                w.Write(sb.ToString().TrimEnd() + "\n")

    // --- Reader: Markdown -> Document (line-based subset) -------------------------

    /// Reads a Markdown subset (headings, fenced code, lists, quotes, rules,
    /// paragraphs) into a fluid `Document`. Inline emphasis is preserved verbatim as
    /// text; richer inline parsing can be layered on later without changing the model.
    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]
            member _.Read(input, _ctx) =
                use r = new StreamReader(input, Encoding.UTF8, true, 1024, leaveOpen = true)
                let text = r.ReadToEnd().Replace("\r\n", "\n")
                let lines = text.Split('\n')
                let blocks = ResizeArray<Block>()
                let mutable i = 0
                let paragraphBuffer = ResizeArray<string>()
                let listBuffer = ResizeArray<string>()
                let mutable listOrdered = false

                let flushParagraph () =
                    if paragraphBuffer.Count > 0 then
                        let inlines =
                            paragraphBuffer
                            |> Seq.mapi (fun idx line ->
                                if idx = 0 then [ Run(line, TextStyle.Default) ]
                                else [ LineBreak; Run(line, TextStyle.Default) ])
                            |> Seq.toList
                            |> List.concat
                        blocks.Add(Paragraph(inlines, None))
                        paragraphBuffer.Clear()

                let flushList () =
                    if listBuffer.Count > 0 then
                        let items = listBuffer |> Seq.map (fun s -> { Content = [ Doc.para s ] }) |> Seq.toList
                        blocks.Add(ListBlock { Ordered = listOrdered; Start = None; Items = items })
                        listBuffer.Clear()

                let flushAll () = flushParagraph (); flushList ()

                while i < lines.Length do
                    let line = lines.[i]
                    let trimmed = line.TrimStart()
                    if trimmed.StartsWith("```") then
                        flushAll ()
                        let lang = trimmed.Substring(3).Trim()
                        let codeLines = ResizeArray<string>()
                        i <- i + 1
                        while i < lines.Length && not (lines.[i].TrimStart().StartsWith("```")) do
                            codeLines.Add(lines.[i])
                            i <- i + 1
                        let langOpt = if lang = "" then None else Some lang
                        blocks.Add(CodeBlock(langOpt, String.Join("\n", codeLines)))
                    elif trimmed.StartsWith("#") then
                        flushAll ()
                        let level = trimmed.Length - trimmed.TrimStart('#').Length
                        let content = trimmed.TrimStart('#').Trim()
                        blocks.Add(Heading(level, [ Run(content, TextStyle.Default) ]))
                    elif trimmed = "---" || trimmed = "***" || trimmed = "___" then
                        flushAll ()
                        blocks.Add(ThematicBreak)
                    elif trimmed.StartsWith("> ") then
                        flushAll ()
                        blocks.Add(Quote [ Doc.para (trimmed.Substring(2)) ])
                    elif trimmed.StartsWith("- ") || trimmed.StartsWith("* ") then
                        flushParagraph ()
                        if listOrdered then flushList ()
                        listOrdered <- false
                        listBuffer.Add(trimmed.Substring(2))
                    elif trimmed.Length > 2 && Char.IsDigit trimmed.[0] && trimmed.Contains(". ") then
                        flushParagraph ()
                        if not listOrdered then flushList ()
                        listOrdered <- true
                        listBuffer.Add(trimmed.Substring(trimmed.IndexOf(". ") + 2))
                    elif trimmed = "" then
                        flushAll ()
                    else
                        flushList ()
                        paragraphBuffer.Add(line)
                    i <- i + 1

                flushAll ()
                Document.OfBlocks (List.ofSeq blocks)

    /// A `ConverterRegistry` preconfigured with the built-in text formats
    /// (markdown + plain text), ready for A → unified → B conversions.
    let defaultRegistry () =
        let reg = ConverterRegistry()
        reg.RegisterReader(Reader())
        reg.RegisterWriter(Writer())
        reg.RegisterReader(PlainText.Reader())
        reg.RegisterWriter(PlainText.Writer())
        reg
