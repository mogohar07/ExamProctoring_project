namespace ExamProctoring.Api.Errors;

// ProblemDetails responses with a stable "code" (§6.7) written outside controllers, such as the 401 and 403
// responses of the authentication and authorization middleware.
public static class ProblemResponses
{
    public static async Task WriteAsync(HttpContext context, int statusCode, string code, string detail)
    {
        context.Response.StatusCode = statusCode;

        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = statusCode,
                Detail = detail,
                Extensions = { [ErrorResults.CodeExtension] = code },
            },
        });
    }
}
