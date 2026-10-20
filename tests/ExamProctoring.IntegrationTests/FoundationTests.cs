using System.Net;
using System.Text.Json;

namespace ExamProctoring.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class FoundationTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_IsHealthy_WhenDatabaseIsReachable()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenApiDocument_LoadsInDevelopment()
    {
        var response = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.StartsWith("3.", document.RootElement.GetProperty("openapi").GetString());
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetails()
    {
        var response = await _client.GetAsync("/api/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
