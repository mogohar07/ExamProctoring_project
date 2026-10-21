using ExamProctoring.Api.Authorization;
using ExamProctoring.Api.Errors;
using ExamProctoring.Application.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ExamProctoring.Api.Controllers;

// Account administration (§6.9). Admins only; Admin accounts are never created, listed or managed here (D21).
[ApiController]
[Route("api/admin")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public sealed class AdminAccountsController(AccountAdminService accountAdminService) : ControllerBase
{
    // §6.9, D23. Lists other people's accounts, so the response is marked not cacheable.
    [HttpGet("accounts")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<AccountListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<IActionResult> ListAccounts([FromQuery] AccountListQuery query, CancellationToken cancellationToken)
    {
        var result = await accountAdminService.ListAccountsAsync(query, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // §6.9, D21, D23. Same fields as a list item; marked not cacheable.
    [HttpGet("accounts/{id}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<AccountSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetAccount(string id, CancellationToken cancellationToken)
    {
        var result = await accountAdminService.GetAccountAsync(id, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // §6.4, D18. The 201 response is the only time the temporary password is shown; it is marked not cacheable.
    [HttpPost("instructors")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<CreateInstructorResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> CreateInstructor(CreateInstructorRequest request, CancellationToken cancellationToken)
    {
        var result = await accountAdminService.CreateInstructorAsync(request, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : this.ToProblem(result.Error!);
    }

    // §6.4, D18, D19. The 200 response is the only time the temporary password is shown; it is marked not cacheable.
    [HttpPost("accounts/{id}/temporary-password")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<TemporaryPasswordResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> IssueTemporaryPassword(string id, CancellationToken cancellationToken)
    {
        var result = await accountAdminService.IssueTemporaryPasswordAsync(id, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // §6.4, D22. The reason is required.
    [HttpPost("accounts/{id}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Deactivate(string id, DeactivateAccountRequest request, CancellationToken cancellationToken)
    {
        var result = await accountAdminService.DeactivateAsync(id, request, cancellationToken);

        return result.IsSuccess ? NoContent() : this.ToProblem(result.Error!);
    }

    // §6.4, D22. The reason is optional, so the body may be left out.
    [HttpPost("accounts/{id}/reactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Reactivate(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReactivateAccountRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await accountAdminService.ReactivateAsync(id, request, cancellationToken);

        return result.IsSuccess ? NoContent() : this.ToProblem(result.Error!);
    }
}
