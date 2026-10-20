using Microsoft.AspNetCore.Authorization;

namespace ExamProctoring.Api.Authorization;

// §6.1 check 2: while MustChangePassword is set, only endpoints marked [AllowedDuringPasswordChange] are allowed.
// Part of the default, fallback and role policies, so it applies to every protected endpoint.
public sealed class PasswordChangeRequirement : IAuthorizationRequirement;

public sealed class PasswordChangeRequirementHandler : AuthorizationHandler<PasswordChangeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PasswordChangeRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext;
        var passwordChangeRequired = httpContext is not null && AccountGate.IsPasswordChangeRequired(httpContext);
        var endpointAllowed = httpContext?.GetEndpoint()?.Metadata.GetMetadata<AllowedDuringPasswordChangeAttribute>() is not null;

        if (!passwordChangeRequired || endpointAllowed)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
