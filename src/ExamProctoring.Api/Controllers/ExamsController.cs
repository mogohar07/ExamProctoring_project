using ExamProctoring.Api.Authorization;
using ExamProctoring.Api.Errors;
using ExamProctoring.Api.RateLimiting;
using ExamProctoring.Application.Exams;
using ExamProctoring.Application.Exams.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ExamProctoring.Api.Controllers;

// Instructor exam management (§7, D25-D30). Instructors only, and only their own exams; Admins and Students get 403.
[ApiController]
[Route("api/exams")]
[Authorize(Policy = AuthorizationPolicies.InstructorOnly)]
public sealed class ExamsController(ExamService examService, ExamImportService importService) : ControllerBase
{
    // D29: paged, optional status filter, newest start first.
    [HttpGet]
    [ProducesResponseType<ExamListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<IActionResult> ListExams([FromQuery] ExamListQuery query, CancellationToken cancellationToken)
    {
        var result = await examService.ListAsync(query, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // D25, D26: a new Draft exam.
    [HttpPost]
    [ProducesResponseType<ExamResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<IActionResult> CreateExam(ExamRequest request, CancellationToken cancellationToken)
    {
        var result = await examService.CreateAsync(request, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : this.ToProblem(result.Error!);
    }

    // Owner decision 2026-10-25: the reviewed exam (for example from the Word import) saved in one step as a new Draft
    // exam with its questions. All or nothing; never published. Questions may have no marks until publishing.
    [HttpPost("with-questions")]
    [ProducesResponseType<DraftExamResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<IActionResult> CreateExamWithQuestions(DraftExamRequest request, CancellationToken cancellationToken)
    {
        var result = await examService.CreateWithQuestionsAsync(request, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : this.ToProblem(result.Error!);
    }

    [HttpGet("{id}")]
    [ProducesResponseType<ExamResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetExam(string id, CancellationToken cancellationToken)
    {
        var result = await examService.GetAsync(id, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // D27: full replacement of the editable fields while the exam has not started.
    [HttpPut("{id}")]
    [ProducesResponseType<ExamResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> UpdateExam(string id, ExamRequest request, CancellationToken cancellationToken)
    {
        var result = await examService.UpdateAsync(id, request, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // D34, D36: a valid Draft that has not started becomes Published.
    [HttpPost("{id}/publish")]
    [ProducesResponseType<ExamResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> PublishExam(string id, CancellationToken cancellationToken)
    {
        var result = await examService.PublishAsync(id, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // D36: a Published exam that has not started returns to Draft.
    [HttpPost("{id}/unpublish")]
    [ProducesResponseType<ExamResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> UnpublishExam(string id, CancellationToken cancellationToken)
    {
        var result = await examService.UnpublishAsync(id, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // D30, D36: a Published exam that has not started is cancelled for good.
    [HttpPost("{id}/cancel")]
    [ProducesResponseType<ExamResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> CancelExam(string id, CancellationToken cancellationToken)
    {
        var result = await examService.CancelAsync(id, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // D30: Draft exams only.
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> DeleteExam(string id, CancellationToken cancellationToken)
    {
        var result = await examService.DeleteAsync(id, cancellationToken);

        return result.IsSuccess ? NoContent() : this.ToProblem(result.Error!);
    }

    // Word import (owner decision 2026-10-24): returns the questions of an existing .docx for the instructor to review.
    // Stateless: nothing is stored and nothing is published; the app saves the reviewed questions with
    // POST api/exams/{examId}/questions. The upload is kept in memory and its content is never logged.
    [HttpPost("import/docx")]
    [EnableRateLimiting(RateLimitPolicies.DocxImport)]
    [DisableFormValueBinding]
    [RequestSizeLimit(ExamImportService.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2 * ExamImportService.MaxRequestBytes,
        MemoryBufferThreshold = (int)(2 * ExamImportService.MaxRequestBytes))]
    [ProducesResponseType<DocxImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> ImportDocx(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
            return this.ToProblem(ExamImportService.InvalidFile(ExamImportService.FileRequiredMessage));

        IFormCollection form;
        try
        {
            // A body over the Kestrel limit throws BadHttpRequestException (413), handled by GlobalExceptionHandler.
            form = await Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return this.ToProblem(ExamImportService.InvalidFile(ExamImportService.UnreadableUploadMessage));
        }

        var file = form.Files.GetFile("file");
        if (file is null)
            return this.ToProblem(ExamImportService.InvalidFile(ExamImportService.FileRequiredMessage));
        if (form.Files.Count > 1)
            return this.ToProblem(ExamImportService.InvalidFile(ExamImportService.OneFileMessage));
        if (file.Length > ExamImportService.MaxFileBytes)
            return this.PayloadTooLarge($"The file is larger than {ExamImportService.MaxFileBytes / (1024 * 1024)} MB.");

        await using var content = file.OpenReadStream();
        var result = importService.Import(file.FileName, file.Length, content, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }
}
