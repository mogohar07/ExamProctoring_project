using ExamProctoring.Application.Common;
using Microsoft.Extensions.Options;

namespace ExamProctoring.Infrastructure.Identity;

// Rejects missing or invalid Jwt settings at startup, with messages that never include the key.
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var errors = options.GetValidationErrors();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
