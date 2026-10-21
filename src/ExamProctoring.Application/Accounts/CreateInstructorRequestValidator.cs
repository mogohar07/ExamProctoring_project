using ExamProctoring.Application.Auth;
using FluentValidation;

namespace ExamProctoring.Application.Accounts;

// The same full-name and email rules as student sign-up (§2.2).
public sealed class CreateInstructorRequestValidator : AbstractValidator<CreateInstructorRequest>
{
    public CreateInstructorRequestValidator()
    {
        RuleFor(r => r.FullName).ValidFullName();

        RuleFor(r => r.Email).ValidEmail();
    }
}
