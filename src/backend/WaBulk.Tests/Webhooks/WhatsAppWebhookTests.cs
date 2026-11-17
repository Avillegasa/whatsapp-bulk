using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Webhooks;
using WaBulk.Webhooks.Security;

namespace WaBulk.Tests.Webhooks;

public sealed class WhatsAppWebhookTests : IAsyncLifetime
{
    private const string AppSecret = "app-secret-de-prueba";
    private const string VerifyToken = "verify-token-de-prueba";
    private const string Route = WhatsAppWebhookEndpoints.Route;

    // Payload de estado con caracteres no ASCII: la firma debe calcularse sobre los bytes crudos.
    private const string StatusPayload = """
        {"object":"whatsapp_business_account","entry":[{"id":"2088365445132575","changes":[{"value":{"messaging_product":"whatsapp","metadata":{"display_phone_number":"15551892694","phone_number_id":"1524289747428097"},"statuses":[{"id":"wamid.TEST","status":"delivered","timestamp":"1763325639","recipient_id":"59170000000","biz_opaque_callback_data":"01a1e617-2789-7751-99a1-2d17ae607b7b"}]},"field":"messages"}]}],"nota":"canción ñandú"}
        """;

    private readonly string _dbName = $"webhooks-{Guid.NewGuid()}";
    private WebApplicationFactory<WebhooksAssemblyMarker> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<WebhooksAssemblyMarker>().WithWebHostBuilder(web =>
        {
            // "Testing": no carga user-secrets reales.
            web.UseEnvironment("Testing");
            web.UseSetting("Meta:AppSecret", AppSecret);
            web.UseSetting("Meta:VerifyToken", VerifyToken);
            web.UseSetting("Webhooks:MaxBodyBytes", "4096");
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<WaBulkDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<WaBulkDbContext>>();
                services.AddDbContext<WaBulkDbContext>(o => o.UseInMemoryDatabase(_dbName));
            });
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<List<WebhookInbox>> InboxAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WaBulkDbContext>();
        return await db.WebhookInbox.AsNoTracking().ToListAsync();
    }

    private static HttpRequestMessage Post(byte[] body, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new("application/json");
        if (signature is not null)
        {
            request.Headers.Add(WebhookSignature.HeaderName, signature);
        }

        return request;
    }

    [Fact]
    public async Task Post_ValidSignature_Returns200_AndStoresRawBody()
    {
        var body = Encoding.UTF8.GetBytes(StatusPayload);

        var response = await _client.SendAsync(Post(body, WebhookSignature.Compute(body, AppSecret)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await InboxAsync());
        Assert.Equal(StatusPayload, row.Body);
        Assert.Equal(WebhookInboxStatus.Pendiente, row.Status);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task Post_InvalidSignature_Returns401_AndStoresNothing()
    {
        var body = Encoding.UTF8.GetBytes(StatusPayload);

        var response = await _client.SendAsync(Post(body, WebhookSignature.Compute(body, "otro-secreto")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await InboxAsync());
    }

    [Fact]
    public async Task Post_MissingSignature_Returns401_AndStoresNothing()
    {
        var response = await _client.SendAsync(Post(Encoding.UTF8.GetBytes(StatusPayload), signature: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await InboxAsync());
    }

    [Fact]
    public async Task Post_BodyModifiedAfterSigning_Returns401()
    {
        var signed = Encoding.UTF8.GetBytes(StatusPayload);
        var tampered = Encoding.UTF8.GetBytes(StatusPayload.Replace("delivered", "read", StringComparison.Ordinal));

        var response = await _client.SendAsync(Post(tampered, WebhookSignature.Compute(signed, AppSecret)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await InboxAsync());
    }

    [Fact]
    public async Task Post_BodyOverLimit_Returns413_AndStoresNothing()
    {
        var body = new byte[5000];
        Array.Fill(body, (byte)'a');

        var response = await _client.SendAsync(Post(body, WebhookSignature.Compute(body, AppSecret)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await InboxAsync());
    }

    [Fact]
    public async Task Get_ValidVerifyToken_ReturnsChallenge()
    {
        var response = await _client.GetAsync(new Uri(
            $"{Route}?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=1158201444", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1158201444", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("subscribe", "token-equivocado")]
    [InlineData("unsubscribe", VerifyToken)]
    public async Task Get_InvalidModeOrToken_Returns403(string mode, string token)
    {
        var response = await _client.GetAsync(new Uri(
            $"{Route}?hub.mode={mode}&hub.verify_token={token}&hub.challenge=1158201444", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
