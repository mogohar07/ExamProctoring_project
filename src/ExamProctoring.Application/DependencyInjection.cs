using ExamProctoring.Application.Auth;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ExamProctoring.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddScoped<AuthService>();

        return services;
    }
}
