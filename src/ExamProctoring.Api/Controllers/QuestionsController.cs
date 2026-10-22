using ExamProctoring.Api.Authorization;
using ExamProctoring.Api.Errors;
using ExamProctoring.Application.Exams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamProctoring.Api.Controllers;

// Questions of the instructor's own exams (§7, D31-D35). Instructors only; Admins and Students get 403.
[ApiController]
[Route("api")]
[Authorize(Policy = AuthorizationPolicies.InstructorOnly)]
public sealed class QuestionsController(QuestionService questionService) : ControllerBase
{
    // S2, S4: every question of the exam in position order, whatever the exam's status or time.
    [HttpGet("exams/{examId}/questions")]
    [ProducesResponseType<QuestionListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> ListQuestions(string examId, CancellationToken cancellationToken)
    {
        var result = await questionService.ListAsync(examId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // D33: the complete question with its options, appended to the exam.
    [HttpPost("exams/{examId}/questions")]
    [ProducesResponseType<QuestionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> CreateQuestion(string examId, QuestionRequest request, CancellationToken cancellationToken)
    {
        var result = await questionService.CreateAsync(examId, request, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : this.ToProblem(result.Error!);
    }

    // D33: full replacement of the question and its options.
    [HttpPut("questions/{id}")]
    [ProducesResponseType<QuestionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> UpdateQuestion(string id, QuestionRequest request, CancellationToken cancellationToken)
    {
        var result = await questionService.UpdateAsync(id, request, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // D33, D34: later questions move up; a Published exam keeps at least one question.
    [HttpDelete("questions/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> DeleteQuestion(string id, CancellationToken cancellationToken)
    {
        var result = await questionService.DeleteAsync(id, cancellationToken);

        return result.IsSuccess ? NoContent() : this.ToProblem(result.Error!);
    }
}
