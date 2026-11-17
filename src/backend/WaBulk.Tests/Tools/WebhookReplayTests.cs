using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Persistence;
using WaBulk.WebhookReplay;
using WaBulk.Webhooks;
using WaBulk.Webhooks.Security;

namespace WaBulk.Tests.Tools;

/// <summary>
/// Herramienta de replay (F0.8): plantillas válidas, firma aceptada por el receptor y resultado
/// esperado de cada escenario tras pasar por el receptor y el procesador del inbox.
/// </summary>
public sealed class WebhookReplayTests : IAsyncLifetime
{
    private const string AppSecret = "app-secret-de-prueba";
    private static readonly DateTime Start = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _dbName = $"replay-{Guid.NewGuid()}";
    private WebApplicationFactory<WebhooksAssemblyMarker> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<WebhooksAssemblyMarker>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.UseSetting("Meta:AppSecret", AppSecret);
            web.UseSetting("Meta:VerifyToken", "verify-token-de-prueba");
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<WaBulkDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<WaBulkDbContext>>();
                services.AddDbContext<WaBulkDbContext>(o => o.UseInMemoryDatabase(_dbName, _root));
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

    private static ReplayContext Context(Guid attemptId, string wamid) =>
        new("2088365445132575", "1524289747428097", "15551892694", "59170000000", wamid, attemptId, 1763380800);

    private WaBulkDbContext Db() =>
        new(new DbContextOptionsBuilder<WaBulkDbContext>().UseInMemoryDatabase(_dbName, _root).Options);

    private ReplaySender Sender(bool wrongSignature = false) =>
        new(_client, new Uri(_client.BaseAddress!, WhatsAppWebhookEndpoints.Route), AppSecret, wrongSignature, TimeSpan.Zero);

    private async Task<SendAttempt> SeedAttemptAsync()
    {
        await using var db = Db();
        var job = new DispatchJob
        {
            CampaignVersionId = 1,
            ContactId = 1,
            PhoneE164 = "+59170000000",
            ScheduledUtc = Start,
            ExpiresUtc = Start.AddDays(1),
        };
        db.DispatchJobs.Add(job);
        await db.SaveChangesAsync();
        var attempt = new SendAttempt
        {
            AttemptId = Guid.CreateVersion7(),
            DispatchJobId = job.Id,
            StartedUtc = Start,
            Status = SendAttemptStatus.Accepted,
            Wamid = "wamid.REPLAYTEST",
        };
        db.SendAttempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt;
    }

    private async Task ProcessInboxAsync()
    {
        await using var db = Db();
        var processor = new WebhookInboxProcessor(db, Options.Create(new InboxProcessorOptions()),
            Options.Create(new OptOutOptions()), TimeProvider.System, NullLogger<WebhookInboxProcessor>.Instance);
        await processor.ProcessBatchAsync(CancellationToken.None);
    }

    [Fact]
    public void Cada_plantilla_genera_json_valido_sin_marcadores_y_firma_aceptada_por_el_receptor()
    {
        var ctx = Context(Guid.CreateVersion7(), "wamid.X");
        var names = PayloadTemplates.Names().ToList();
        Assert.Equal(9, names.Count);

        foreach (var name in names)
        {
            var body = PayloadTemplates.Render(name, ctx);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("whatsapp_business_account", doc.RootElement.GetProperty("object").GetString());
            Assert.DoesNotContain("{{", System.Text.Encoding.UTF8.GetString(body), StringComparison.Ordinal);
            Assert.True(WebhookSignature.IsValid(body, ReplaySigner.Sign(body, AppSecret), AppSecret), name);
            Assert.False(WebhookSignature.IsValid(body, ReplaySigner.SignWrong(body, AppSecret), AppSecret), name);
        }
    }

    [Fact]
    public void Los_escenarios_pedidos_en_el_plan_existen_y_usan_plantillas_existentes()
    {
        var names = PayloadTemplates.Names().ToHashSet();
        foreach (var scenario in new[] { "duplicado", "desorden", "lote", "stop", "resume", "plantilla-paused", "baja-palabra", "baja-boton" })
        {
            var s = ReplayScenarios.Find(scenario);
            Assert.NotNull(s);
            Assert.All(s.Steps, step => Assert.Contains(step.Payload, names));
        }
    }

    [Fact]
    public async Task Firma_mala_responde_401_y_no_guarda()
    {
        var results = await Sender(wrongSignature: true).RunAsync(ReplayScenarios.Find("lote")!, Context(Guid.CreateVersion7(), "wamid.X"));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Unauthorized, r.Status));
        await using var db = Db();
        Assert.Equal(0, await db.WebhookInbox.CountAsync());
    }

    [Fact]
    public async Task Duplicado_dos_filas_en_inbox_y_un_solo_evento()
    {
        var attempt = await SeedAttemptAsync();
        var results = await Sender().RunAsync(ReplayScenarios.Find("duplicado")!, Context(attempt.AttemptId, attempt.Wamid!));
        await ProcessInboxAsync();

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        await using var db = Db();
        Assert.Equal(2, await db.WebhookInbox.CountAsync());
        var events = await db.MessageEvents.ToListAsync();
        Assert.Equal(DeliveryStatus.Delivered, Assert.Single(events).Status);
        Assert.Equal(DeliveryStatus.Delivered, (await db.SendAttempts.SingleAsync()).DeliveryStatus);
    }

    [Fact]
    public async Task Desorden_tres_eventos_y_el_estado_no_retrocede()
    {
        var attempt = await SeedAttemptAsync();
        await Sender().RunAsync(ReplayScenarios.Find("desorden")!, Context(attempt.AttemptId, attempt.Wamid!));
        await ProcessInboxAsync();

        await using var db = Db();
        Assert.Equal(3, await db.WebhookInbox.CountAsync());
        Assert.Equal(3, await db.MessageEvents.CountAsync());
        Assert.Equal(DeliveryStatus.Read, (await db.SendAttempts.SingleAsync()).DeliveryStatus);
    }

    [Fact]
    public async Task Lote_una_fila_y_tres_eventos()
    {
        var attempt = await SeedAttemptAsync();
        await Sender().RunAsync(ReplayScenarios.Find("lote")!, Context(attempt.AttemptId, attempt.Wamid!));
        await ProcessInboxAsync();

        await using var db = Db();
        Assert.Equal(1, await db.WebhookInbox.CountAsync());
        Assert.Equal(3, await db.MessageEvents.CountAsync());
        Assert.Equal(DeliveryStatus.Read, (await db.SendAttempts.SingleAsync()).DeliveryStatus);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("resume")]
    [InlineData("plantilla-paused")]
    public async Task Sin_statuses_se_guarda_y_queda_procesado_sin_eventos(string scenario)
    {
        var results = await Sender().RunAsync(ReplayScenarios.Find(scenario)!, Context(Guid.CreateVersion7(), "wamid.X"));
        await ProcessInboxAsync();

        Assert.Equal(HttpStatusCode.OK, Assert.Single(results).Status);
        await using var db = Db();
        Assert.Equal(WebhookInboxStatus.Procesado, (await db.WebhookInbox.SingleAsync()).Status);
        Assert.Equal(0, await db.MessageEvents.CountAsync());
    }

    [Theory]
    [InlineData("stop", 0, 1)]
    [InlineData("resume", 0, 1)]
    [InlineData("baja-palabra", 1, 0)]
    [InlineData("baja-boton", 1, 0)]
    public async Task Escenarios_de_baja_registran_la_capa_que_corresponde(string scenario, int consents, int preferences)
    {
        await Sender().RunAsync(ReplayScenarios.Find(scenario)!, Context(Guid.CreateVersion7(), "wamid.X"));
        await ProcessInboxAsync();

        await using var db = Db();
        Assert.Equal(consents, await db.ConsentRecords.CountAsync());
        Assert.Equal(preferences, await db.PlatformPreferences.CountAsync());
        Assert.All(await db.ConsentRecords.ToListAsync(), c => Assert.Equal("+59170000000", c.PhoneE164));
    }
}
