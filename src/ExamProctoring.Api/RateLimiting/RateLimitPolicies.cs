using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ExamProctoring.Api.RateLimiting;

// Per-IP fixed-window limits (§6.7): the anonymous auth endpoints (register 5/min, login 10/min, refresh 30/min) and
// the Word import (10/min). Rate limiting runs before authentication, so the client IP is the only key available.
public static class RateLimitPolicies
{
    public const string Register = "register";
    public const string Login = "login";
    public const string Refresh = "refresh";
    public const string DocxImport = "docx-import";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // An empty 429 response; UseStatusCodePages turns it into ProblemDetails.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(Register, context => PerIpPerMinute(context, permitLimit: 5));
            options.AddPolicy(Login, context => PerIpPerMinute(context, permitLimit: 10));
            options.AddPolicy(Refresh, context => PerIpPerMinute(context, permitLimit: 30));
            options.AddPolicy(DocxImport, context => PerIpPerMinute(context, permitLimit: 10));
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
