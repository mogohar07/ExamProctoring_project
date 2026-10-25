using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using Microsoft.Extensions.Logging;

namespace ExamProctoring.Application.Exams.Import;

// POST /api/exams/import/docx (owner decision 2026-10-24): reads the questions of an existing Word document for the
// instructor to review. Stateless: it never creates an exam, saves a question, stores the file or publishes anything,
// and it never logs document content. This is not AI generation of questions from lecture material.
public sealed class ExamImportService(
    IDocxTextExtractor extractor,
    IExamSettings settings,
    ILogger<ExamImportService> logger)
{
    // The largest file accepted, and the largest request body (the file plus the multipart framing).
    public const long MaxFileBytes = 10L * 1024 * 1024;
    public const long MaxRequestBytes = MaxFileBytes + 64 * 1024;

    // More questions than any exam needs; protects the response size.
    public const int MaxQuestions = 500;

    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    public const string FileField = "File";
    public const string FileRequiredMessage = "A .docx file is required.";
    public const string OneFileMessage = "Upload exactly one file.";
    public const string EmptyFileMessage = "The file is empty.";
    public const string UnreadableUploadMessage = "The upload could not be read.";

    public Result<DocxImportResponse> Import(string? fileName, long length, Stream? content, CancellationToken cancellationToken)
    {
        if (content is null)
            return Result<DocxImportResponse>.Failure(InvalidFile(FileRequiredMessage));
        if (length == 0)
            return Result<DocxImportResponse>.Failure(InvalidFile(EmptyFileMessage));
        if (!string.Equals(Path.GetExtension(fileName), ".docx", StringComparison.OrdinalIgnoreCase))
            return Result<DocxImportResponse>.Failure(Unsupported("Only Word .docx files can be imported."));

        DocxReadResult read;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(ReadTimeout);
            try
            {
                read = extractor.Read(content, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Word import stopped: the document took longer than {Seconds} s to read.", ReadTimeout.TotalSeconds);
                return Result<DocxImportResponse>.Failure(Unreadable("The document took too long to read."));
            }
        }

        if (read.Failure is { } failure)
        {
            logger.LogInformation("Word import refused a file: {Failure}.", failure);
            return Result<DocxImportResponse>.Failure(failure == DocxReadFailure.Unsupported
                ? Unsupported(read.FailureMessage ?? "The file is not a Word .docx document.")
                : Unreadable(read.FailureMessage ?? "The document could not be read."));
        }

        var response = QuestionImportParser.Parse(read.Items, settings.MaxMcqOptions);
        if (response.Summary.Found > MaxQuestions)
            return Result<DocxImportResponse>.Failure(Unreadable($"The document has more than {MaxQuestions} questions."));

        // Counts only: document content is never logged.
        logger.LogInformation(
            "Word import read {Found} questions ({Ready} ready, {NeedsAttention} need attention, {NotImported} not imported).",
            response.Summary.Found, response.Summary.Ready, response.Summary.NeedsAttention, response.Summary.NotImported);
        return Result<DocxImportResponse>.Success(response);
    }

    public static AppError InvalidFile(string message) =>
        new(ErrorCodes.ValidationFailed, "One or more fields are invalid.",
            new Dictionary<string, string[]> { [FileField] = [message] });

    private static AppError Unsupported(string message) => new(ErrorCodes.ImportFileUnsupported, message);

    private static AppError Unreadable(string message) => new(ErrorCodes.ImportFileUnreadable, message);
}
