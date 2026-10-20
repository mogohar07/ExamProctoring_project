using ExamProctoring.Api.Authorization;
using ExamProctoring.Api.Errors;
using ExamProctoring.Api.RateLimiting;
using ExamProctoring.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ExamProctoring.Api.Controllers;

// Authentication endpoints (§6.9). So far: student sign-up, login and the caller's own account.
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService authService) : ControllerBase
{
    // Request bodies of the token endpoints are small JSON objects (approved 2026-10-20). Not applied to register.
    private const long TokenEndpointMaxBodyBytes = 16 * 1024;

    // §6.4. The only place tokens are issued. An unknown email and a wrong password get the same 401.
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [RequestSizeLimit(TokenEndpointMaxBodyBytes)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // §6.9. Any active user, also while a password change is required (§6.3).
    [HttpGet("me")]
    [Authorize]
    [AllowedDuringPasswordChange]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var result = await authService.GetMeAsync(cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }

    // §6.4. Students only: the role is always Student, whatever the request contains. No tokens are returned;
    // the client logs in afterwards.
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Register)]
    [ProducesResponseType<RegisterStudentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IActionResult> Register(RegisterStudentRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.RegisterStudentAsync(request, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : this.ToProblem(result.Error!);
    }
}
