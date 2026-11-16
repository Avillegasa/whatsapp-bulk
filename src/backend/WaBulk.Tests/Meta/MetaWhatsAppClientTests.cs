using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Meta;

namespace WaBulk.Tests.Meta;

public sealed class MetaWhatsAppClientTests
{
    private static readonly TemplateMessage HelloWorld = new("hello_world", "en_US");

    internal static MetaOptions TestOptions() => new()
    {
        GraphApiVersion = "v99.0",
        BaseUrl = new Uri("https://graph.test/"),
        PhoneNumberId = "123456",
        AccessToken = "token-de-prueba",
    };

    internal static MetaWhatsAppClient CreateClient(FakeHttpHandler handler, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        var monitor = new StaticOptionsMonitor(TestOptions());
        return new MetaWhatsAppClient(http, monitor, NullLogger<MetaWhatsAppClient>.Instance);
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal const string AcceptedJson = """
        {"messaging_product":"whatsapp","contacts":[{"input":"+59170000000","wa_id":"59170000000"}],
         "messages":[{"id":"wamid.TEST123","message_status":"accepted"}]}
        """;

    [Fact]
    public async Task Status200_WithMessageId_IsAccepted()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, AcceptedJson)));
        var client = CreateClient(handler);

        var result = await client.SendTemplateAsync("+59170000000", HelloWorld, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SendAttemptStatus.Accepted, result.Status);
        Assert.Equal("wamid.TEST123", result.Wamid);
        Assert.Equal(200, result.HttpStatus);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task Request_UsesConfiguredVersion_AndCarriesAttemptIdAsCallbackData()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, AcceptedJson)));
        var client = CreateClient(handler);
        var attemptId = Guid.NewGuid();

        await client.SendTemplateAsync("+59170000000", HelloWorld, attemptId, CancellationToken.None);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://graph.test/v99.0/123456/messages", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("template", root.GetProperty("type").GetString());
        Assert.Equal("+59170000000", root.GetProperty("to").GetString());
        Assert.Equal("hello_world", root.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal("en_US", root.GetProperty("template").GetProperty("language").GetProperty("code").GetString());
        Assert.Equal(attemptId.ToString(), root.GetProperty("biz_opaque_callback_data").GetString());
    }

    [Fact]
    public async Task Error131026_IsRejected_WithCode()
    {
        const string errorJson = """
            {"error":{"message":"(#131026) Message undeliverable","type":"OAuthException","code":131026,
             "error_data":{"messaging_product":"whatsapp","details":"Message Undeliverable."},"fbtrace_id":"Axyz"}}
            """;
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest, errorJson)));
        var client = CreateClient(handler);

        var result = await client.SendTemplateAsync("+59170000000", HelloWorld, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SendAttemptStatus.Rejected, result.Status);
        Assert.Equal(131026, result.ErrorCode);
        Assert.Equal(400, result.HttpStatus);
        Assert.Null(result.Wamid);
        Assert.Contains("fbtrace_id=Axyz", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_IsSubmissionUnknown()
    {
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Json(HttpStatusCode.OK, AcceptedJson);
        });
        var client = CreateClient(handler, timeout: TimeSpan.FromMilliseconds(100));

        var result = await client.SendTemplateAsync("+59170000000", HelloWorld, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SendAttemptStatus.SubmissionUnknown, result.Status);
        Assert.Null(result.Wamid);
        Assert.Null(result.HttpStatus);
    }

    [Fact]
    public async Task ConnectionDropped_IsSubmissionUnknown()
    {
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("connection reset"));
        var client = CreateClient(handler);

        var result = await client.SendTemplateAsync("+59170000000", HelloWorld, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SendAttemptStatus.SubmissionUnknown, result.Status);
    }

    [Fact]
    public async Task Status500_WithoutErrorBody_IsSubmissionUnknown()
    {
        var handler = new FakeHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("") }));
        var client = CreateClient(handler);

        var result = await client.SendTemplateAsync("+59170000000", HelloWorld, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SendAttemptStatus.SubmissionUnknown, result.Status);
        Assert.Equal(502, result.HttpStatus);
    }

    [Fact]
    public async Task Status200_WithoutMessageId_IsSubmissionUnknown()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var client = CreateClient(handler);

        var result = await client.SendTemplateAsync("+59170000000", HelloWorld, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SendAttemptStatus.SubmissionUnknown, result.Status);
    }

    private sealed class StaticOptionsMonitor(MetaOptions value) : IOptionsMonitor<MetaOptions>
    {
        public MetaOptions CurrentValue => value;
        public MetaOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<MetaOptions, string?> listener) => null;
    }
}
