using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Exams.Import;
using Microsoft.Extensions.Logging;
using Charts = DocumentFormat.OpenXml.Drawing.Charts;
using MathMl = DocumentFormat.OpenXml.Math;
using Pictures = DocumentFormat.OpenXml.Drawing.Pictures;

namespace ExamProctoring.Infrastructure.Documents;

// Reads a Word (.docx) document for the exam import with DocumentFormat.OpenXml. Works on an in-memory copy, checks
// the package before parsing (ZIP signature, number of parts, real unpacked size, macros), and returns the text in
// reading order. Images, equations, text boxes, charts, embedded objects and complex tables are reported as
// unsupported content, never guessed. Nothing is stored and no document content is logged.
public sealed class OpenXmlDocxTextExtractor(ILogger<OpenXmlDocxTextExtractor> logger) : IDocxTextExtractor
{
    public const int MaxParts = 1000;
    public const long MaxUnpackedBytes = 50L * 1024 * 1024;
    public const long MaxCharactersInPart = 20_000_000;

    // Automatic list numbers above this are written as plain digits, so a large start value in the file cannot
    // generate a long label ("AAAA..." for letters). A start below 0 is not valid Word numbering: the file is refused.
    public const int MaxListNumber = 999;

    // A cell that only holds a marker ("1." "A)" "*B)" "س1" "أ)") may be joined with the text cell next to it.
    private const int MaxMarkerCellLength = 12;

    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0];

    public DocxReadResult Read(Stream document, CancellationToken cancellationToken)
    {
        try
        {
            return ReadDocument(document, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or OpenXmlPackageException or XmlException
                                              or IOException or InvalidOperationException or ArgumentException
                                              or FormatException or NotSupportedException)
        {
            return Unreadable("The file is damaged or is not a valid .docx document.");
        }
        catch (Exception exception)
        {
            // Only the type is logged: the message or stack trace of an unexpected exception could carry file content.
            logger.LogWarning("Word import could not read a file: unexpected {ExceptionType}.", exception.GetType().FullName);
            return Unreadable("The file is damaged or is not a valid .docx document.");
        }
    }

    private static DocxReadResult ReadDocument(Stream document, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        document.CopyTo(memory);
        memory.Position = 0;

        if (StartsWith(memory, OleSignature))
            return Unreadable("The file is password-protected or is not a valid .docx document.");
        if (!StartsWith(memory, ZipSignature))
            return DocxReadResult.Fail(DocxReadFailure.Unsupported, "The file is not a Word .docx document.");

        if (CheckPackage(memory, cancellationToken) is { } packageProblem)
            return packageProblem;

        memory.Position = 0;
        using var word = WordprocessingDocument.Open(memory, false,
            new OpenSettings { MaxCharactersInPart = MaxCharactersInPart });

        if (word.DocumentType != WordprocessingDocumentType.Document)
            return DocxReadResult.Fail(DocxReadFailure.Unsupported,
                "Only Word .docx documents are supported (not macro-enabled documents or templates).");

        var body = word.MainDocumentPart?.Document?.Body;
        if (body is null)
            return Unreadable("The file has no document content.");

        var items = new List<DocxItem>();
        var numbering = new NumberingResolver(word.MainDocumentPart!.NumberingDefinitionsPart?.Numbering);
        AddBlocks(body.ChildElements, items, numbering, cancellationToken);
        return DocxReadResult.Success(items);
    }

    private static DocxReadResult Unreadable(string message) => DocxReadResult.Fail(DocxReadFailure.Unreadable, message);

    private static bool StartsWith(MemoryStream stream, byte[] signature)
    {
        var buffer = stream.GetBuffer();
        return stream.Length >= signature.Length && buffer.AsSpan(0, signature.Length).SequenceEqual(signature);
    }

    // Reads every part of the package with a byte budget, so a ZIP bomb is stopped by what is really unpacked, not
    // by the sizes the archive declares.
    private static DocxReadResult? CheckPackage(MemoryStream memory, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaxParts)
            return Unreadable($"The document has more than {MaxParts} internal parts.");

        if (zip.Entries.Any(entry => entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
            return DocxReadResult.Fail(DocxReadFailure.Unsupported, "Macro-enabled Word documents are not supported.");

        var buffer = new byte[81920];
        long unpacked = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = entry.Open();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                unpacked += read;
                if (unpacked > MaxUnpackedBytes)
                    return Unreadable("The document is too large when unpacked.");
            }
        }
        return null;
    }

    private static void AddBlocks(
        IEnumerable<OpenXmlElement> elements, List<DocxItem> items, NumberingResolver numbering, CancellationToken cancellationToken)
    {
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case Paragraph paragraph:
                    AddParagraph(paragraph, items, numbering);
                    break;
                case Table table:
                    AddTable(table, items, numbering);
                    break;
                case SdtBlock contentControl:
                    if (contentControl.SdtContentBlock is { } block)
                        AddBlocks(block.ChildElements, items, numbering, cancellationToken);
                    break;
                case CustomXmlBlock customXml:
                    AddBlocks(customXml.ChildElements, items, numbering, cancellationToken);
                    break;
            }
        }
    }

    private static void AddParagraph(Paragraph paragraph, List<DocxItem> items, NumberingResolver numbering)
    {
        var content = ParagraphContent.Read(paragraph);
        var prefix = numbering.Prefix(paragraph);
        var lines = SplitLines(content.Text);

        if (prefix is not null)
        {
            if (lines.Count == 0)
                lines.Add(prefix);
            else
                lines[0] = prefix + " " + lines[0];
        }

        items.AddRange(lines.Select(DocxItem.Line));
        foreach (var kind in content.Unsupported)
            items.Add(DocxItem.UnsupportedContent(kind));
    }

    // A table is imported only when its structure is unambiguous: no nested tables, no merged cells, no images,
    // equations or other objects, and each row is either one text cell or a marker cell next to one text cell.
    // Anything else is reported as an unsupported table, with its text kept as unassigned text.
    private static void AddTable(Table table, List<DocxItem> items, NumberingResolver numbering)
    {
        var simple = !table.Descendants<Table>().Any();
        var lines = new List<string>();
        var allText = new List<string>();

        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<List<string>>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var properties = cell.TableCellProperties;
                if (properties?.GridSpan?.Val?.Value > 1 || properties?.VerticalMerge is not null)
                    simple = false;

                var cellLines = new List<string>();
                foreach (var paragraph in cell.Elements<Paragraph>())
                {
                    var content = ParagraphContent.Read(paragraph);
                    if (content.Unsupported.Count > 0)
                        simple = false;
                    var prefix = numbering.Prefix(paragraph);
                    var paragraphLines = SplitLines(content.Text);
                    if (prefix is not null)
                    {
                        if (paragraphLines.Count == 0)
                            paragraphLines.Add(prefix);
                        else
                            paragraphLines[0] = prefix + " " + paragraphLines[0];
                    }
                    cellLines.AddRange(paragraphLines);
                }
                if (cell.Elements().Any(e => e is not Paragraph and not TableCellProperties))
                    simple = false;
                if (cellLines.Count > 0)
                    cells.Add(cellLines);
            }

            allText.Add(string.Join(" | ", cells.Select(cellLines => string.Join(" ", cellLines))));

            switch (cells.Count)
            {
                case 0:
                    break;
                case 1:
                    lines.AddRange(cells[0]);
                    break;
                case 2 when IsMarkerCell(cells[0]) && cells[1].Count == 1:
                    lines.Add(cells[0][0] + " " + cells[1][0]);
                    break;
                default:
                    simple = false;
                    break;
            }
        }

        if (simple)
            items.AddRange(lines.Select(DocxItem.Line));
        else
            items.Add(DocxItem.UnsupportedContent(UnsupportedContentKind.Table, allText.Where(text => text.Length > 0).ToList()));
    }

    private static bool IsMarkerCell(List<string> cellLines) =>
        cellLines.Count == 1
        && cellLines[0].Length <= MaxMarkerCellLength
        && (cellLines[0].Length <= 3 || cellLines[0][^1] is '.' or ')' or ':' or '-' or '–');

    private static List<string> SplitLines(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    // The visible text of one paragraph (runs, tabs, line breaks) and the content that cannot be imported.
    private sealed class ParagraphContent
    {
        private readonly StringBuilder _text = new();

        public List<UnsupportedContentKind> Unsupported { get; } = [];

        public string Text => _text.ToString();

        public static ParagraphContent Read(Paragraph paragraph)
        {
            var content = new ParagraphContent();
            foreach (var child in paragraph.ChildElements)
                content.Visit(child);
            return content;
        }

        private void Visit(OpenXmlElement element)
        {
            switch (element)
            {
                case Text text:
                    _text.Append(text.Text);
                    return;
                case TabChar:
                    _text.Append(' ');
                    return;
                case Break or CarriageReturn:
                    _text.Append('\n');
                    return;
                case Drawing drawing:
                    AddUnsupported(KindOfDrawing(drawing));
                    return;
                case Picture picture:
                    AddUnsupported(picture.Descendants().Any(e => e.LocalName == "textbox")
                        ? UnsupportedContentKind.Other
                        : UnsupportedContentKind.Image);
                    return;
                case AlternateContent alternate:
                    var drawingInside = alternate.Descendants<Drawing>().FirstOrDefault();
                    AddUnsupported(drawingInside is null ? UnsupportedContentKind.Other : KindOfDrawing(drawingInside));
                    return;
                case EmbeddedObject:
                    AddUnsupported(UnsupportedContentKind.Other);
                    return;
                case MathMl.OfficeMath or MathMl.Paragraph:
                    AddUnsupported(UnsupportedContentKind.Equation);
                    return;
                case TextBoxContent:
                    AddUnsupported(UnsupportedContentKind.Other);
                    return;
                default:
                    foreach (var child in element.ChildElements)
                        Visit(child);
                    return;
            }
        }

        private static UnsupportedContentKind KindOfDrawing(Drawing drawing)
        {
            if (drawing.Descendants<Charts.ChartReference>().Any())
                return UnsupportedContentKind.Other;
            return drawing.Descendants<Pictures.Picture>().Any() ? UnsupportedContentKind.Image : UnsupportedContentKind.Other;
        }

        private void AddUnsupported(UnsupportedContentKind kind)
        {
            if (!Unsupported.Contains(kind))
                Unsupported.Add(kind);
        }
    }

    // Recreates the visible text of Word's automatic numbering ("1.", "A)", "أ-") from numbering.xml, so numbered
    // lists keep their question numbers and option letters. Counters are kept per list instance (numId); bullets and
    // unknown formats produce no prefix.
    private sealed class NumberingResolver(Numbering? numbering)
    {
        private const string ArabicAbjad = "أبجدهوزحطيكلمنسعفصقرشتثخذضظغ";
        private const string ArabicAlpha = "أبتثجحخدذرزسشصضطظعغفقكلمنهوي";
        private readonly Dictionary<int, int?[]> _counters = [];

        public string? Prefix(Paragraph paragraph)
        {
            var properties = paragraph.ParagraphProperties?.NumberingProperties;
            var numId = properties?.NumberingId?.Val?.Value;
            if (numbering is null || numId is null or 0)
                return null;
            var levelIndex = Math.Clamp(properties!.NumberingLevelReference?.Val?.Value ?? 0, 0, 8);

            var instance = numbering.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var abstractId = instance?.AbstractNumId?.Val?.Value;
            var definition = numbering.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
            if (instance is null || definition is null)
                return null;

            Level? LevelAt(int i) => definition.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == i);
            int StartAt(int i)
            {
                var start = instance.Elements<LevelOverride>().FirstOrDefault(o => o.LevelIndex?.Value == i)?.StartOverrideNumberingValue?.Val?.Value
                            ?? LevelAt(i)?.StartNumberingValue?.Val?.Value
                            ?? 1;
                // Caught by Read as an unreadable file.
                return start >= 0 ? start : throw new InvalidDataException("A list starts below 0.");
            }

            if (!_counters.TryGetValue(numId.Value, out var counters))
                _counters[numId.Value] = counters = new int?[9];
            counters[levelIndex] = counters[levelIndex] is { } count ? count + 1 : StartAt(levelIndex);
            for (var deeper = levelIndex + 1; deeper < counters.Length; deeper++)
                counters[deeper] = null;

            var level = LevelAt(levelIndex);
            var format = level?.NumberingFormat?.Val?.Value;
            if (level?.LevelText?.Val?.Value is not { Length: > 0 } pattern
                || format is null || format == NumberFormatValues.Bullet || format == NumberFormatValues.None)
                return null;

            var result = new StringBuilder(pattern);
            for (var i = 0; i < 9; i++)
            {
                var placeholder = "%" + (i + 1);
                if (pattern.Contains(placeholder, StringComparison.Ordinal))
                    result.Replace(placeholder, Format(counters[i] ?? StartAt(i), LevelAt(i)?.NumberingFormat?.Val?.Value));
            }
            var prefix = result.ToString().Trim();
            return prefix.Length > 0 ? prefix : null;
        }

        private static string Format(int value, NumberFormatValues? format)
        {
            // Outside 0-999 the number is written as digits: at most 11 characters, whatever the file says.
            if (value is < 0 or > MaxListNumber)
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (format == NumberFormatValues.UpperLetter)
                return Letters(value, 'A');
            if (format == NumberFormatValues.LowerLetter)
                return Letters(value, 'a');
            if (format == NumberFormatValues.ArabicAbjad)
                return ArabicAbjad[(value - 1 + ArabicAbjad.Length) % ArabicAbjad.Length].ToString();
            if (format == NumberFormatValues.ArabicAlpha)
                return ArabicAlpha[(value - 1 + ArabicAlpha.Length) % ArabicAlpha.Length].ToString();
            if (format == NumberFormatValues.UpperRoman)
                return Roman(value);
            if (format == NumberFormatValues.LowerRoman)
                return Roman(value).ToLowerInvariant();
            if (format == NumberFormatValues.DecimalZero)
                return value.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Word repeats the letter after Z: 27 is AA, 28 is BB.
        private static string Letters(int value, char first)
        {
            var index = (Math.Max(value, 1) - 1) % 26;
            var repeat = (Math.Max(value, 1) - 1) / 26 + 1;
            return new string((char)(first + index), repeat);
        }

        private static string Roman(int value)
        {
            if (value is < 1 or > 3999)
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            (int Value, string Symbol)[] numerals =
                [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
            var builder = new StringBuilder();
            foreach (var (numeral, symbol) in numerals)
            {
                while (value >= numeral)
                {
                    builder.Append(symbol);
                    value -= numeral;
                }
            }
            return builder.ToString();
        }
    }
}
