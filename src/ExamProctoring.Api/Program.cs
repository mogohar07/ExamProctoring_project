using ExamProctoring.Api.Authorization;
using ExamProctoring.Api.Errors;
using ExamProctoring.Api.Middleware;
using ExamProctoring.Api.RateLimiting;
using ExamProctoring.Application;
using ExamProctoring.Application.Common;
using ExamProctoring.Infrastructure;
using ExamProctoring.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

const string WebExamCorsPolicy = "WebExamApp";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = ErrorResults.InvalidModelState);

// Errors: every failure becomes an RFC 9457 ProblemDetails response.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// OpenAPI document at /openapi/v1.json (served in Development only, see below).
builder.Services.AddOpenApi();

// CORS: only the configured web exam app origins (§6.7). Empty list = no cross-origin browser access.
var webExamOrigins = builder.Configuration.GetSection("Cors:WebExamOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(WebExamCorsPolicy, policy =>
    policy.WithOrigins(webExamOrigins).AllowAnyHeader().AllowAnyMethod()));

// Per-IP limits on the anonymous auth endpoints (§6.7).
builder.Services.AddApiRateLimiting();

// JWT bearer authentication with the per-request account gate; fallback, default and role policies (§6.1, §6.7).
builder.Services.AddApiAuthentication();
builder.Services.AddApiAuthorization();

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

// Fail fast, before touching the database, if the Jwt settings are missing or invalid (§6.8, D12).
// The error lists what is wrong without ever including the signing key.
_ = app.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;

// Roles and the first admin (§6.8, D20). Fails startup if no admin exists and the Seed settings are missing or invalid.
// Needs the migrations applied first; they are never applied here.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // Anonymous, or the fallback policy would require a token for the document.
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ExamProctoring API v1"));
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors(WebExamCorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
