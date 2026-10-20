using System.Security.Claims;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;

namespace ExamProctoring.Api.Authorization;

// ICurrentUser from the validated access token's claims (§6.7).
public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public Guid UserId => Guid.TryParse(Principal.FindFirst(AuthClaimTypes.Subject)?.Value, out var userId)
        ? userId
        : throw new InvalidOperationException("The access token has no valid subject.");

    public string Role => Principal.FindFirst(AuthClaimTypes.Role)?.Value
        ?? throw new InvalidOperationException("The access token has no role.");

    private ClaimsPrincipal Principal =>
        httpContextAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } user
            ? user
            : throw new InvalidOperationException("The request has no authenticated user.");
}
