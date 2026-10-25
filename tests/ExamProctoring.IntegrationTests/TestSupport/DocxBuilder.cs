using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace ExamProctoring.IntegrationTests.TestSupport;

// Builds Word (.docx) documents in memory for the import tests, so no binary fixtures are stored in the repository.
public sealed class DocxBuilder
{
    private readonly List<OpenXmlElement> _blocks = [];
    private readonly List<(int NumId, int AbstractId)> _instances = [];
    private readonly List<AbstractNum> _abstracts = [];

    // One paragraph; "\n" becomes a line break inside the paragraph.
    public DocxBuilder Paragraph(string text)
    {
        _blocks.Add(new Paragraph(TextRun(text)));
        return this;
    }

    public DocxBuilder Paragraphs(params string[] lines)
    {
        foreach (var line in lines)
            Paragraph(line);
        return this;
    }

    // A paragraph in a Word list (automatic numbering): the number or letter is not part of the text.
    public DocxBuilder Numbered(int numId, string text)
    {
        _blocks.Add(new Paragraph(
            new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = numId })),
            TextRun(text)));
        return this;
    }

    // A list definition: format and level text such as "%1." or "%1)". Several numIds may share one definition; each
    // numId keeps its own counter, as Word does when numbering is restarted.
    public DocxBuilder List(int abstractId, NumberFormatValues format, string levelText, params int[] numIds) =>
        ListStartingAt(abstractId, format, levelText, 1, numIds);

    // A list definition whose first number is start (any value, including ones Word would not write).
    public DocxBuilder ListStartingAt(int abstractId, NumberFormatValues format, string levelText, int start, params int[] numIds)
    {
        _abstracts.Add(new AbstractNum(
            new Level(new StartNumberingValue { Val = start }, new NumberingFormat { Val = format }, new LevelText { Val = levelText })
            { LevelIndex = 0 })
        { AbstractNumberId = abstractId });
        foreach (var numId in numIds)
            _instances.Add((numId, abstractId));
        return this;
    }

    public DocxBuilder ParagraphWithImage(string text)
    {
        var picture = new A.Graphic(new A.GraphicData(new PIC.Picture(new PIC.NonVisualPictureProperties(
                new PIC.NonVisualDrawingProperties { Id = 1U, Name = "image" }, new PIC.NonVisualPictureDrawingProperties())))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });
        _blocks.Add(new Paragraph(TextRun(text), new Run(new Drawing(new DW.Inline(
            new DW.Extent { Cx = 100, Cy = 100 }, new DW.DocProperties { Id = 1U, Name = "image" }, picture)))));
        return this;
    }

    public DocxBuilder ParagraphWithEquation(string text)
    {
        _blocks.Add(new Paragraph(TextRun(text), new M.OfficeMath(new M.Run(new M.Text("x = 1")))));
        return this;
    }

    // Each row is a list of cell texts.
    public DocxBuilder Table(params string[][] rows)
    {
        var table = new Table();
        foreach (var row in rows)
            table.Append(new TableRow(row.Select(text => new TableCell(new Paragraph(TextRun(text)))).ToArray()));
        _blocks.Add(table);
        return this;
    }

    // A table whose first cell spans two columns.
    public DocxBuilder MergedCellTable(string spanningText, string otherText)
    {
        _blocks.Add(new Table(
            new TableRow(new TableCell(new TableCellProperties(new GridSpan { Val = 2 }), new Paragraph(TextRun(spanningText)))),
            new TableRow(new TableCell(new Paragraph(TextRun(otherText))), new TableCell(new Paragraph(TextRun(otherText))))));
        return this;
    }

    public byte[] Build(WordprocessingDocumentType type = WordprocessingDocumentType.Document)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, type))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(_blocks.Select(block => block.CloneNode(true)).ToArray()));

            if (_abstracts.Count > 0)
            {
                var numbering = main.AddNewPart<NumberingDefinitionsPart>();
                numbering.Numbering = new Numbering(
                    _abstracts.Select(a => (OpenXmlElement)a.CloneNode(true))
                        .Concat(_instances.Select(i => new NumberingInstance(new AbstractNumId { Val = i.AbstractId }) { NumberID = i.NumId }))
                        .ToArray());
            }
        }
        return stream.ToArray();
    }

    // Adds extra entries to an existing package (for ZIP-bomb and part-count tests).
    public static byte[] WithExtraEntries(byte[] docx, int count, long bytesPerEntry)
    {
        using var stream = new MemoryStream();
        stream.Write(docx);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var zeros = new byte[64 * 1024];
            for (var i = 0; i < count; i++)
            {
                using var entry = zip.CreateEntry($"customXml/extra{i}.bin", CompressionLevel.SmallestSize).Open();
                for (long written = 0; written < bytesPerEntry; written += zeros.Length)
                    entry.Write(zeros, 0, (int)Math.Min(zeros.Length, bytesPerEntry - written));
            }
        }
        return stream.ToArray();
    }

    private static Run TextRun(string text)
    {
        var run = new Run();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                run.Append(new Break());
            run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return run;
    }
}
