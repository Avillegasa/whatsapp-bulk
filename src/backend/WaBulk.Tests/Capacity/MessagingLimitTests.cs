using System.Net;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Capacity;
using WaBulk.Domain.Messaging;
using WaBulk.Tests.Meta;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Capacity;

/// <summary>Valores de Meta del límite de mensajería (F1.7, D-026).</summary>
public sealed class MessagingTiersTests
{
    [Theory]
    [InlineData("TIER_50", 50)]
    [InlineData("TIER_250", 250)]
    [InlineData("TIER_2K", 2000)]
    [InlineData("TIER_10K", 10000)]
    [InlineData("TIER_100K", 100000)]
    [InlineData("TIER_UNLIMITED", int.MaxValue)]
    [InlineData("2000", 2000)]   // El ejemplo del webhook manda un número.
    [InlineData("-1", int.MaxValue)]
    [InlineData("TIER_NOT_SET", null)]
    [InlineData("", null)]
    [InlineData("algo", null)]
    public void Parse(string value, int? expected) => Assert.Equal(expected, MessagingTiers.Parse(value));

    [Fact]
    public void Presupuesto_sin_limite_no_desborda()
    {
        Assert.True(CapacityBudget.Compute(int.MaxValue, 90, 200) > 1_000_000_000);
    }
}

/// <summary>Límite real de Meta por webhook y por GET, y su uso en la capacidad (contra SQL Server).</summary>
[Trait("Category", "Integration")]
public sealed class MessagingLimitIntegrationTests(SqlServerTestDatabase database)
    : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
    private readonly ManualClock _clock = new(Start);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Webhook_business_capability_update_actualiza_el_limite_y_la_capacidad_lo_usa()
    {
        await ProcessAsync(Webhook("business_capability_update", "\"max_daily_conversations_per_business\": 2000, \"max_phone_numbers_per_waba\": 25"));

        await using var db = database.CreateContext();
        var row = await db.MessagingLimits.SingleAsync();
        Assert.Equal(("TEST", 2000, "2000", "WebhookCapacidad"), (row.PortfolioId, row.Limit, row.MetaValue, row.Source));
        // La capacidad usa el límite real (2000), no Capacity:PortfolioLimit (100 000 en TestCapacity.Unlimited).
        Assert.Equal(2000, await TestCapacity.Unlimited(db).BudgetAsync(CancellationToken.None));
        Assert.Equal(WebhookInboxStatus.Procesado, (await db.WebhookInbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task Calidad_del_numero_sin_el_campo_se_ignora_y_TIER_250_se_aplica()
    {
        await ProcessAsync(Webhook("phone_number_quality_update", "\"display_phone_number\": \"15551892694\", \"event\": \"ONBOARDING\""));
        await using (var db = database.CreateContext())
        {
            Assert.Empty(await db.MessagingLimits.ToListAsync());
        }

        await ProcessAsync(Webhook("phone_number_quality_update",
            "\"display_phone_number\": \"15551892694\", \"event\": \"THROUGHPUT_UPGRADE\", \"max_daily_conversations_per_business\": \"TIER_250\""));
        await using var check = database.CreateContext();
        Assert.Equal((250, "WebhookCalidad"), ((await check.MessagingLimits.SingleAsync()).Limit, (await check.MessagingLimits.SingleAsync()).Source));
    }

    [Fact]
    public async Task GET_a_Meta_guarda_el_limite()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
            """{"whatsapp_business_manager_messaging_limit":"TIER_2K","id":"123456"}""")));
        await using var db = database.CreateContext();

        var value = await TestServices.Limits(db, _clock).RefreshFromApiAsync(MetaWhatsAppClientTests.CreateClient(handler), CancellationToken.None);

        Assert.Equal("TIER_2K", value);
        Assert.Equal("https://graph.test/v99.0/123456?fields=whatsapp_business_manager_messaging_limit",
            Assert.Single(handler.Requests).Request.RequestUri!.ToString());
        await using var check = database.CreateContext();
        Assert.Equal((2000, "Api"), ((await check.MessagingLimits.SingleAsync()).Limit, (await check.MessagingLimits.SingleAsync()).Source));
    }

    [Fact]
    public async Task GET_con_error_no_cambia_el_limite()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"Invalid OAuth access token","code":190}}""")));
        await using var db = database.CreateContext();

        Assert.Null(await TestServices.Limits(db, _clock).RefreshFromApiAsync(MetaWhatsAppClientTests.CreateClient(handler), CancellationToken.None));
        Assert.Empty(await db.MessagingLimits.ToListAsync());
    }

    private static string Webhook(string field, string valueFields) =>
        "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"id\":\"2088365445132575\",\"time\":1763474400,"
        + "\"changes\":[{\"field\":\"" + field + "\",\"value\":{" + valueFields + "}}]}]}";

    private async Task ProcessAsync(string body)
    {
        await using var db = database.CreateContext();
        db.WebhookInbox.Add(new WebhookInbox { ReceivedUtc = Start, Body = body });
        await db.SaveChangesAsync();
        await TestServices.Processor(db, _clock).ProcessBatchAsync(CancellationToken.None);
    }
}
