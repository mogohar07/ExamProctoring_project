using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ExamProctoring.Api.RateLimiting;

// Per-IP fixed-window limits for the anonymous auth endpoints (§6.7). Login and refresh are added with their endpoints.
public static class RateLimitPolicies
{
    public const string Register = "register";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // An empty 429 response; UseStatusCodePages turns it into ProblemDetails.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Register: 5 requests per minute per IP address.
            options.AddPolicy(Register, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        return services;
    }
}
