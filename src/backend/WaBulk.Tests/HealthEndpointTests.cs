using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using WaBulk.Api;
using WaBulk.Webhooks;

namespace WaBulk.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Api_Health_ReturnsHealthy()
    {
        await using var factory = new WebApplicationFactory<ApiAssemblyMarker>();
        await AssertHealthyAsync(factory.CreateClient());
    }

    [Fact]
    public async Task Webhooks_Health_ReturnsHealthy()
    {
        await using var factory = new WebApplicationFactory<WebhooksAssemblyMarker>();
        await AssertHealthyAsync(factory.CreateClient());
    }

    private static async Task AssertHealthyAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
