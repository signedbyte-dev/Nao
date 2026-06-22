namespace Nao.Documents.Tests

open System.IO
open System.IO.Compression
open System.Text
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Documents

/// End-to-end conversion tests for the unified document model. These assert the same
/// things the old smoke-docs.fsx script printed: every md→target conversion produces
/// a well-formed artifact, and the binary formats round-trip back to Markdown.
[<TestClass>]
type ConversionTests() =

    static let sampleMarkdown =
        "# Sample Report\n\n"
        + "A short paragraph with **bold**, *italic* and `code`.\n\n"
        + "## Items\n\n"
        + "- First item\n"
        + "- Second item\n\n"
        + "### Code\n\n"
        + "```fsharp\nlet x = 42\n```\n\n"
        + "> A quoted line.\n\n"
        + "| Name | Score |\n| --- | --- |\n| Alice | 10 |\n| Bob | 7 |\n\n"
        + "---\n\n"
        + "Final paragraph.\n"

    let registry = Formats.fullRegistry ()
    let ctx = ConversionContext.InMemory

    let bytesOf (s: string) = Encoding.UTF8.GetBytes s
    let convert (src: string) (dst: string) (input: byte[]) =
        Converter.convertBytes registry src dst ctx input
    let mdToBytes (dst: string) = convert Markdown.MediaType dst (bytesOf sampleMarkdown)

    /// Assert an OOXML (zip) package opens and contains every required part.
    let assertPackageParts (bytes: byte[]) (required: string list) =
        use ms = new MemoryStream(bytes)
        use archive = new ZipArchive(ms, ZipArchiveMode.Read)
        let entries = archive.Entries |> Seq.map (fun e -> e.FullName) |> Set.ofSeq
        for part in required do
            Assert.IsTrue(entries.Contains part, sprintf "Missing OOXML part: %s" part)

    [<TestMethod>]
    member _.RegistryExposesExpectedReadableTypes() =
        let readable = registry.ReadableTypes
        for mt in [ PlainText.MediaType; Markdown.MediaType; Html.MediaType; Docx.MediaType ] do
            Assert.IsTrue(List.contains mt readable, sprintf "Expected readable type: %s" mt)

    [<TestMethod>]
    member _.RegistryExposesExpectedWritableTypes() =
        let writable = registry.WritableTypes
        for mt in [ PlainText.MediaType; Markdown.MediaType; Html.MediaType
                    Docx.MediaType; Pdf.MediaType; Xlsx.MediaType; Pptx.MediaType ] do
            Assert.IsTrue(List.contains mt writable, sprintf "Expected writable type: %s" mt)

    [<TestMethod>]
    member _.MarkdownConvertsToEveryTarget_ProducesNonEmptyOutput() =
        for target in [ Html.MediaType; Pdf.MediaType; Docx.MediaType
                        Xlsx.MediaType; Pptx.MediaType; PlainText.MediaType ] do
            let bytes = mdToBytes target
            Assert.IsTrue(bytes.Length > 0, sprintf "Empty output for target %s" target)

    [<TestMethod>]
    member _.MarkdownToPlainText_ContainsTextContent() =
        let text = Encoding.UTF8.GetString(mdToBytes PlainText.MediaType)
        Assert.IsTrue(text.Contains "Sample Report", "Plain text lost the heading")
        Assert.IsTrue(text.Contains "First item", "Plain text lost the list item")

    [<TestMethod>]
    member _.MarkdownToHtml_IsWellFormedHtml() =
        let html = Encoding.UTF8.GetString(mdToBytes Html.MediaType)
        Assert.IsTrue(html.Contains "<h1", "HTML missing <h1> heading")
        Assert.IsTrue(html.Contains "Sample Report", "HTML missing heading text")
        Assert.IsTrue(html.Contains "<li", "HTML missing list items")

    [<TestMethod>]
    member _.MarkdownToPdf_HasValidStructure() =
        let pdf = mdToBytes Pdf.MediaType
        let text = Encoding.Latin1.GetString pdf
        Assert.IsTrue(text.StartsWith "%PDF-1.", "PDF missing %PDF header")
        Assert.IsTrue(text.TrimEnd().EndsWith "%%EOF", "PDF missing %%EOF trailer")
        Assert.IsTrue(text.Contains "xref", "PDF missing cross-reference table")

    [<TestMethod>]
    member _.MarkdownToDocx_PackageHasRequiredParts() =
        assertPackageParts
            (mdToBytes Docx.MediaType)
            [ "[Content_Types].xml"; "_rels/.rels"; "word/document.xml"; "word/styles.xml" ]

    [<TestMethod>]
    member _.MarkdownToXlsx_PackageHasRequiredParts() =
        assertPackageParts
            (mdToBytes Xlsx.MediaType)
            [ "[Content_Types].xml"; "xl/workbook.xml"; "xl/worksheets/sheet1.xml" ]

    [<TestMethod>]
    member _.MarkdownToPptx_PackageHasRequiredParts() =
        assertPackageParts
            (mdToBytes Pptx.MediaType)
            [ "[Content_Types].xml"; "ppt/presentation.xml"
              "ppt/slideMasters/slideMaster1.xml"; "ppt/slideMasters/theme/theme1.xml" ]

    [<TestMethod>]
    member _.HtmlRoundTripsBackToMarkdown() =
        let html = mdToBytes Html.MediaType
        let md = Encoding.UTF8.GetString(convert Html.MediaType Markdown.MediaType html)
        Assert.IsTrue(md.Contains "Sample Report", "HTML→Markdown lost the heading")
        Assert.IsTrue(md.Contains "First item", "HTML→Markdown lost the list item")

    [<TestMethod>]
    member _.DocxRoundTripsBackToMarkdown() =
        let docx = mdToBytes Docx.MediaType
        let md = Encoding.UTF8.GetString(convert Docx.MediaType Markdown.MediaType docx)
        Assert.IsTrue(md.Contains "Sample Report", "DOCX→Markdown lost the heading")
        Assert.IsTrue(md.Contains "First item", "DOCX→Markdown lost the list item")
