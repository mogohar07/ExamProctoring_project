using ExamProctoring.Api.Errors;
using ExamProctoring.Api.Middleware;
using ExamProctoring.Api.RateLimiting;
using ExamProctoring.Application;
using ExamProctoring.Infrastructure;
using ExamProctoring.Infrastructure.Persistence;

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

// Placeholders: Phase 1 adds the JWT bearer scheme, role policies and the fallback policy (§6).
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

// Roles must exist before the first sign-up (§6.8). Needs the migrations applied first; they are never applied here.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
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
