using ExamProctoring.Api.Errors;
using ExamProctoring.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace ExamProctoring.Api.Authorization;

// Shapes the authorization middleware's outcome:
// - a pending password change gives 403 password_change_required (§6.1 check 2);
// - a failed authentication gives the JwtBearer challenge (401 token_invalid, see AuthenticationSetup);
// - a wrong role gives 403 ProblemDetails (§6.1 check 3);
// - a request that matches no endpoint is not challenged: there is nothing to protect, and it ends as 404.
public sealed class ProblemDetailsAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (context.GetEndpoint() is null)
        {
            await next(context);
            return;
        }

        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<PasswordChangeRequirement>().Any() == true)
        {
            await ProblemResponses.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.PasswordChangeRequired,
                "You must change your password before using this endpoint.");
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
