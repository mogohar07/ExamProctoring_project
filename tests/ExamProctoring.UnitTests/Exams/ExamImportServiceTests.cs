using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Application.Exams.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamProctoring.UnitTests.Exams;

// Word import service: file checks, extractor failures, the question cap and the timeout (owner decision 2026-10-24).
public sealed class ExamImportServiceTests
{
    private sealed class FakeExtractor(Func<CancellationToken, DocxReadResult> read) : IDocxTextExtractor
    {
        public int Calls { get; private set; }

        public DocxReadResult Read(Stream document, CancellationToken cancellationToken)
        {
            Calls++;
            return read(cancellationToken);
        }
    }

    private sealed class Settings(int maxMcqOptions) : IExamSettings
    {
        public int WarningLimit => 20;
        public int MaxMcqOptions => maxMcqOptions;
    }

    private static ExamImportService Service(IDocxTextExtractor extractor, int maxMcqOptions = 8) =>
        new(extractor, new Settings(maxMcqOptions), NullLogger<ExamImportService>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Stream SomeBytes() => new MemoryStream([1, 2, 3]);

    private static FakeExtractor Returning(params string[] lines) =>
        new(_ => DocxReadResult.Success(lines.Select(DocxItem.Line).ToList()));

    [Fact]
    public void AValidDocument_ReturnsTheParsedQuestions()
    {
        var result = Service(Returning("1. Q", "*A) x", "B) y", "Marks: 1")).Import("exam.docx", 3, SomeBytes(), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Summary.Ready);
    }

    [Fact]
    public void TheConfiguredMcqMaximum_IsUsed()
    {
        var extractor = Returning("1. Q", "*A) a", "B) b", "C) c", "Marks: 1");

        var result = Service(extractor, maxMcqOptions: 2).Import("exam.docx", 3, SomeBytes(), Ct);

        Assert.Contains(result.Value!.Questions[0].Issues, issue => issue.Code == ImportIssueCodes.TooManyOptions);
    }

    [Fact]
    public void NoFile_IsAValidationError_OnTheFileField()
    {
        var result = Service(Returning()).Import(null, 0, null, Ct);

        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Equal([ExamImportService.FileRequiredMessage], result.Error.FieldErrors![ExamImportService.FileField]);
    }

    [Fact]
    public void AnEmptyFile_IsAValidationError()
    {
        var result = Service(Returning()).Import("exam.docx", 0, new MemoryStream(), Ct);

        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Equal([ExamImportService.EmptyFileMessage], result.Error.FieldErrors![ExamImportService.FileField]);
    }

    [Theory]
    [InlineData("exam.doc")]
    [InlineData("exam.pdf")]
    [InlineData("exam.docm")]
    [InlineData("exam")]
    [InlineData("exam.docx.txt")]
    public void AnotherExtension_IsUnsupported_WithoutReadingTheFile(string fileName)
    {
        var extractor = Returning();

        var result = Service(extractor).Import(fileName, 3, SomeBytes(), Ct);

        Assert.Equal(ErrorCodes.ImportFileUnsupported, result.Error!.Code);
        Assert.Equal(0, extractor.Calls);
    }

    [Fact]
    public void TheExtensionCheck_IgnoresLetterCase()
    {
        Assert.True(Service(Returning()).Import("EXAM.DOCX", 3, SomeBytes(), Ct).IsSuccess);
    }

    [Theory]
    [InlineData(DocxReadFailure.Unsupported, ErrorCodes.ImportFileUnsupported)]
    [InlineData(DocxReadFailure.Unreadable, ErrorCodes.ImportFileUnreadable)]
    public void ExtractorFailures_MapToTheirCodes(DocxReadFailure failure, string code)
    {
        var extractor = new FakeExtractor(_ => DocxReadResult.Fail(failure, "message"));

        var result = Service(extractor).Import("exam.docx", 3, SomeBytes(), Ct);

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal("message", result.Error.Message);
    }

    [Fact]
    public void MoreThanTheMaximumNumberOfQuestions_IsUnreadable()
    {
        var lines = Enumerable.Range(1, ExamImportService.MaxQuestions + 1).Select(i => $"{i % 1000}. Q").ToArray();

        var result = Service(Returning(lines)).Import("exam.docx", 3, SomeBytes(), Ct);

        Assert.Equal(ErrorCodes.ImportFileUnreadable, result.Error!.Code);
    }

    [Fact]
    public void ExactlyTheMaximumNumberOfQuestions_IsAccepted()
    {
        var lines = Enumerable.Range(1, ExamImportService.MaxQuestions).Select(i => $"{i}. Q").ToArray();

        Assert.True(Service(Returning(lines)).Import("exam.docx", 3, SomeBytes(), Ct).IsSuccess);
    }

    [Fact]
    public void ReadingThatRunsOutOfTime_IsUnreadable()
    {
        // The extractor sees a token that the service cancels after the read timeout.
        var extractor = new FakeExtractor(token =>
        {
            token.WaitHandle.WaitOne(ExamImportService.ReadTimeout + TimeSpan.FromSeconds(5));
            token.ThrowIfCancellationRequested();
            return DocxReadResult.Success([]);
        });

        var result = Service(extractor).Import("exam.docx", 3, SomeBytes(), Ct);

        Assert.Equal(ErrorCodes.ImportFileUnreadable, result.Error!.Code);
    }

    [Fact]
    public void ACallerCancellation_IsNotTurnedIntoAnError()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var extractor = new FakeExtractor(token =>
        {
            token.ThrowIfCancellationRequested();
            return DocxReadResult.Success([]);
        });

        Assert.Throws<OperationCanceledException>(() => Service(extractor).Import("exam.docx", 3, SomeBytes(), caller.Token));
    }
}
