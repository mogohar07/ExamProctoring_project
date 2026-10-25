using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ExamProctoring.Api.Controllers;

// Stops MVC from reading the form during model binding, so the action reads it itself. Otherwise a body over the size
// limit is caught by model binding and answered with 400 validation_failed instead of the 413 from GlobalExceptionHandler.
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisableFormValueBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
