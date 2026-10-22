using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Accounts;
using ExamProctoring.Application.Auth;
using ExamProctoring.Application.Exams;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ExamProctoring.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddScoped<AccountSessions>();
        services.AddScoped<AccountAdminService>();
        services.AddScoped<ExamService>();
        services.AddScoped<QuestionService>();
        services.AddSingleton<IExamSettings, ConfiguredExamSettings>();
        services.AddScoped<AuthService>();

        return services;
    }
}
