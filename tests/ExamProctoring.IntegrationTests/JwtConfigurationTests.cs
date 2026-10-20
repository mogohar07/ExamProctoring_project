using System.Security.Cryptography;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ExamProctoring.IntegrationTests;

// Jwt settings in the real API host (§6.8, D12): bound from configuration, and startup refuses a bad signing key.
[Collection(ApiCollection.Name)]
public sealed class JwtConfigurationTests(ApiFactory factory)
{
    [Fact]
    public void Host_BindsTheJwtSettings_AndTheKeyDecodesTo64Bytes()
    {
        var options = factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        Assert.Equal("ExamProctoring.Api", options.Issuer);
        Assert.Equal("ExamProctoring.Clients", options.Audience);
        Assert.Equal(15, options.AccessTokenMinutes);
        Assert.Equal(7, options.RefreshTokenDays);
        Assert.Equal(64, options.GetSigningKeyBytes().Length);
    }

    [Fact]
    public async Task TokenFromTheHost_PassesTheHostValidationRules()
    {
        var tokens = factory.Services.GetRequiredService<ITokenService>();
        var options = factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), Email = "host@example.com", FullName = "Host Test", SecurityStamp = Guid.NewGuid().ToString("N"),
        };

        var token = tokens.CreateAccessToken(user, RoleNames.Admin).Token;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, JwtTokenValidation.CreateParameters(options));

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Theory]
    [InlineData("missing", "is not configured")]
    [InlineData("malformed", "is not valid Base64")]
    [InlineData("short", "decodes to 31 bytes")]
    public void Startup_Fails_WithAClearMessage_WhenTheSigningKeyIsInvalid(string kind, string expectedMessage)
    {
        var signingKey = kind switch
        {
            "missing" => "",
            "malformed" => "this is not base64!",
            _ => Convert.ToBase64String(RandomNumberGenerator.GetBytes(31)),
        };
        using var badFactory = new SigningKeyFactory(signingKey);

        var exception = Record.Exception(() => badFactory.Services);

        Assert.NotNull(exception);
        var messages = AllMessages(exception);
        Assert.Contains("Jwt:SigningKey", messages);
        Assert.Contains(expectedMessage, messages);
        if (signingKey.Length > 0)
            Assert.DoesNotContain(signingKey, messages);
    }

    private static string AllMessages(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
                messages.AddRange(aggregate.InnerExceptions.Select(e => e.Message));
        }

        return string.Join(Environment.NewLine, messages);
    }

    // Overrides only the signing key (and points at the test database, which startup never reaches when the key is bad).
    private sealed class SigningKeyFactory(string signingKey) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Default", ApiFactory.TestConnectionString);
            builder.UseSetting("Jwt:SigningKey", signingKey);
        }
    }
}
