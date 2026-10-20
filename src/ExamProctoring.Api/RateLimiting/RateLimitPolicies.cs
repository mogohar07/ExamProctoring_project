using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ExamProctoring.Api.RateLimiting;

// Per-IP fixed-window limits for the anonymous auth endpoints (§6.7). Refresh is added with its endpoint.
public static class RateLimitPolicies
{
    public const string Register = "register";
    public const string Login = "login";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // An empty 429 response; UseStatusCodePages turns it into ProblemDetails.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(Register, context => PerIpPerMinute(context, permitLimit: 5));
            options.AddPolicy(Login, context => PerIpPerMinute(context, permitLimit: 10));
        });

        return services;
    }

    private static RateLimitPartition<string> PerIpPerMinute(HttpContext context, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
