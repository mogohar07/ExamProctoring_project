using ExamProctoring.Application.Exams.Import;
using ExamProctoring.Infrastructure.Documents;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.Extensions.Logging;

namespace ExamProctoring.IntegrationTests;

// OpenXmlDocxTextExtractor failure handling (final review 2026-10-25): an unexpected exception gives an unreadable
// file and logs only its type; cancellation still propagates.
public sealed class DocxExtractorTests
{
    private const string Secret = "ZQX7781PRIVATE";

    private sealed class FailingStream(Exception exception) : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override int Read(Span<byte> buffer) => throw exception;
    }

    private static (OpenXmlDocxTextExtractor Extractor, CapturingLoggerProvider Logs, ILoggerFactory Factory) Create()
    {
        var logs = new CapturingLoggerProvider();
        var factory = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        return (new OpenXmlDocxTextExtractor(factory.CreateLogger<OpenXmlDocxTextExtractor>()), logs, factory);
    }

    [Fact]
    public void AnUnexpectedException_GivesAnUnreadableFile_AndLogsOnlyItsType()
    {
        var (extractor, logs, factory) = Create();
        using var _ = factory;

        var result = extractor.Read(new FailingStream(new NotImplementedException($"text from the file: {Secret}")), TestContext.Current.CancellationToken);

        Assert.Equal(DocxReadFailure.Unreadable, result.Failure);
        var entry = Assert.Single(logs.Entries);
        Assert.Contains("System.NotImplementedException", entry);
        Assert.DoesNotContain(Secret, entry);
        Assert.DoesNotContain(" at ", entry);
    }

    [Fact]
    public void AnExpectedDamagedFileException_IsNotLogged()
    {
        var (extractor, logs, factory) = Create();
        using var _ = factory;

        var result = extractor.Read(new FailingStream(new IOException(Secret)), TestContext.Current.CancellationToken);

        Assert.Equal(DocxReadFailure.Unreadable, result.Failure);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public void Cancellation_IsNotTurnedIntoAnUnreadableFile()
    {
        var (extractor, logs, factory) = Create();
        using var _ = factory;

        Assert.Throws<OperationCanceledException>(() =>
            extractor.Read(new FailingStream(new OperationCanceledException()), TestContext.Current.CancellationToken));
        Assert.Empty(logs.Entries);
    }
}
