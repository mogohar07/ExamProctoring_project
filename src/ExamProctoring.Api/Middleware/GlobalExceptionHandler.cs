using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ExamProctoring.Api.Middleware;

// Turns any unhandled exception into a ProblemDetails response (architecture §4.6, §8): 500 for server errors,
// or the client-error status the server reported (for example 413 when a body exceeds an endpoint's size limit).
// The exception message is only shown in Development; stack traces are never returned.
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        int statusCode;
        string title;
        if (exception is BadHttpRequestException badRequest)
        {
            // A problem with the request itself (body too large, malformed or cut short), not a server failure.
            statusCode = badRequest.StatusCode;
            title = statusCode == StatusCodes.Status413PayloadTooLarge
                ? "The request body is too large."
                : "The request could not be processed.";
            logger.LogWarning("Rejected request for {Method} {Path}: {Status} {Message}",
                httpContext.Request.Method, httpContext.Request.Path, statusCode, badRequest.Message);
        }
        else
        {
            statusCode = StatusCodes.Status500InternalServerError;
            title = "An unexpected error occurred.";
            logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = environment.IsDevelopment() ? exception.Message : null,
            },
        });
    }
}
