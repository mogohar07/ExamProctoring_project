using System.Text.Json;
using ExamProctoring.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ExamProctoring.Api.Errors;

// Turns application errors into ProblemDetails responses with a stable "code" extension (§6.7).
public static class ErrorResults
{
    public const string CodeExtension = "code";

    private static readonly Dictionary<string, int> StatusCodesByErrorCode = new()
    {
        [ErrorCodes.ValidationFailed] = StatusCodes.Status400BadRequest,
        [ErrorCodes.DuplicateAccount] = StatusCodes.Status409Conflict,
        [ErrorCodes.TokenInvalid] = StatusCodes.Status401Unauthorized,
    };

    public static IActionResult ToProblem(this ControllerBase controller, AppError error)
    {
        var statusCode = StatusCodesByErrorCode.GetValueOrDefault(error.Code, StatusCodes.Status500InternalServerError);
        var factory = controller.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();

        ProblemDetails problem;
        if (error.FieldErrors is not null)
        {
            // Field names in the same camelCase as the JSON request (fullName, email, ...).
            var modelState = new ModelStateDictionary();
            foreach (var (field, messages) in error.FieldErrors)
            {
                foreach (var message in messages)
                    modelState.AddModelError(JsonNamingPolicy.CamelCase.ConvertName(field), message);
            }

            problem = factory.CreateValidationProblemDetails(controller.HttpContext, modelState, statusCode, detail: error.Message);
        }
        else
        {
            problem = factory.CreateProblemDetails(controller.HttpContext, statusCode, detail: error.Message);
        }

        problem.Extensions[CodeExtension] = error.Code;
        return ProblemResult(problem);
    }

    // [ApiController] model-binding failures (malformed JSON, missing body) use the same code as validator failures.
    public static IActionResult InvalidModelState(ActionContext context)
    {
        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(context.HttpContext, context.ModelState, StatusCodes.Status400BadRequest);
        problem.Extensions[CodeExtension] = ErrorCodes.ValidationFailed;
        return ProblemResult(problem);
    }

    private static ObjectResult ProblemResult(ProblemDetails problem) => new(problem)
    {
        StatusCode = problem.Status,
        ContentTypes = { "application/problem+json" },
    };
}
