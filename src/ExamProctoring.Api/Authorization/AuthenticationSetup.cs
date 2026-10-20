using ExamProctoring.Api.Errors;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace ExamProctoring.Api.Authorization;

// The authentication and authorization pipeline (§4.6, §6.1, §6.7).
public static class AuthenticationSetup
{
    // JWT bearer is the only scheme. Tokens are checked against JwtTokenValidation's rules (issuer, audience,
    // lifetime with 30 s skew, HS256 only), then by the account gate on every request.
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                // Claim names stay exactly as issued: "sub" is the name, "role" the role.
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = JwtTokenValidation.CreateParameters(jwt.Value);
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = AccountGate.ValidateAsync,
                    OnChallenge = WriteUnauthorizedAsync,
                };
            });

        return services;
    }

    // Every endpoint needs an authenticated, active user unless it is marked [AllowAnonymous] (fallback policy);
    // [Authorize] without a policy uses the same rules (default policy); role policies add the role.
    // All of them also enforce the forced password change.
    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(ActiveUser().Build())
            .SetFallbackPolicy(ActiveUser().Build())
            .AddPolicy(AuthorizationPolicies.AdminOnly, ActiveUser().RequireRole(RoleNames.Admin).Build())
            .AddPolicy(AuthorizationPolicies.InstructorOnly, ActiveUser().RequireRole(RoleNames.Instructor).Build())
            .AddPolicy(AuthorizationPolicies.StudentOnly, ActiveUser().RequireRole(RoleNames.Student).Build());

        services.AddSingleton<IAuthorizationHandler, PasswordChangeRequirementHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        return services;
    }

    private static AuthorizationPolicyBuilder ActiveUser() =>
        new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new PasswordChangeRequirement());

    // Missing, malformed, expired or otherwise invalid tokens, and accounts rejected by the gate, all get the
    // same 401 token_invalid, so the response does not reveal which check failed.
    private static async Task WriteUnauthorizedAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
        await ProblemResponses.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, ErrorCodes.TokenInvalid,
            "A valid access token is required.");
    }
}
