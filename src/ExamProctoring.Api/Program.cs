using ExamProctoring.Api.Middleware;
using ExamProctoring.Infrastructure;

const string WebExamCorsPolicy = "WebExamApp";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Errors: every failure becomes an RFC 9457 ProblemDetails response.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// OpenAPI document at /openapi/v1.json (served in Development only, see below).
builder.Services.AddOpenApi();

// CORS: only the configured web exam app origins (§6.7). Empty list = no cross-origin browser access.
var webExamOrigins = builder.Configuration.GetSection("Cors:WebExamOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(WebExamCorsPolicy, policy =>
    policy.WithOrigins(webExamOrigins).AllowAnyHeader().AllowAnyMethod()));

// Placeholders: Phase 1 adds the JWT bearer scheme, role policies and the fallback policy (§6).
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

builder.Services.AddInfrastructure();

var app = builder.Build();

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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
