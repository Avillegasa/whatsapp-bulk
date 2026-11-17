using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Alerts;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Tests.Meta;
using WaBulk.Tests.Support;
using WaBulk.WebhookReplay;

namespace WaBulk.Tests.Alerts;

/// <summary>
/// Alertas y freno de emergencia (F0.12) contra SQL Server. Verificación del plan: una prueba por caso
/// (190, 131042 y plantilla PAUSED): el sistema queda en pausa y se registra la alerta.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertIntegrationTests(SqlServerTestDatabase database)
    : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly ManualClock _clock = new(Start);
    private int _wamid;

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Phone(int n) => $"+5917300{n:0000}";

    private async Task<List<long>> SeedJobsAsync(int count)
    {
        await using var db = database.CreateContext();
        var ids = new List<long>();
        for (var n = 0; n < count; n++)
        {
            db.ConsentRecords.Add(new ConsentRecord
            {
                PhoneE164 = Phone(n), Status = ConsentStatus.OptIn, Source = ConsentSource.Manual,
                RecordedUtc = Start.AddDays(-1), RecordedBy = "prueba",
            });
            await db.SaveChangesAsync();
            var job = await new EnqueueService(db, new ConsentService(db, _clock)).EnqueueAsync(
                new EnqueueRequest(await TestServices.CampaignVersionAsync(db), await TestServices.ContactIdAsync(db, Phone(n)), Phone(n), Start, Start.AddDays(1)), CancellationToken.None);
            ids.Add(job.Id);
        }

        return ids;
    }

    private static FakeHttpHandler Rejecting(int code) => new((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(
        HttpStatusCode.BadRequest,
        $$$"""{"error":{"message":"error {{{code}}}","type":"OAuthException","code":{{{code}}},"error_data":{"details":"x"},"fbtrace_id":"T"}}""")));

    private FakeHttpHandler Accepting() => new((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
        $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.A{{Interlocked.Increment(ref _wamid)}}","message_status":"accepted"}]}""")));

    private async Task<DispatchRound> DispatchAsync(FakeHttpHandler handler)
    {
        await using var db = database.CreateContext();
        var options = Options.Create(new DispatcherOptions
        {
            WorkerId = "W", LeaseSeconds = 120,
        });
        var dispatcher = new Dispatcher(db, new DispatchQueue(db), new ConsentService(db, _clock), TestCapacity.Unlimited(db),
            new AlertService(db, _clock, NullLogger<AlertService>.Instance), TestServices.Lifecycle(db), TestServices.RateLimiter, TestServices.Frequency(), MetaWhatsAppClientTests.CreateClient(handler),
            options, new DispatcherIdentity(options), _clock, NullLogger<Dispatcher>.Instance);
        return await dispatcher.RunOnceAsync(CancellationToken.None);
    }

    private async Task ProcessInboxAsync(params string[] bodies)
    {
        await using var db = database.CreateContext();
        foreach (var body in bodies)
        {
            db.WebhookInbox.Add(new WebhookInbox { ReceivedUtc = Start, Body = body });
        }

        await db.SaveChangesAsync();
        await TestServices.Processor(db, _clock).ProcessBatchAsync(CancellationToken.None);
    }

    /// <summary>Webhook de plantilla PAUSED: el mismo payload que usa tools/webhook-replay (escenario plantilla-paused).</summary>
    private static string TemplatePausedWebhook() => Encoding.UTF8.GetString(PayloadTemplates.Render("template-paused",
        new ReplayContext("2088365445132575", "1524289747428097", "15551892694", "59170000000", "wamid.X", Guid.Empty,
            new DateTimeOffset(Start).ToUnixTimeSeconds())));

    private static string FailedStatusWebhook(int code) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[]
        {
            new
            {
                id = "2088365445132575",
                changes = new[]
                {
                    new
                    {
                        field = "messages",
                        value = new
                        {
                            messaging_product = "whatsapp",
                            statuses = new[]
                            {
                                new
                                {
                                    id = "wamid.OUT", status = "failed", timestamp = "1763380800", recipient_id = "59173000000",
                                    errors = new[] { new { code, title = "There was an error related to your payment method." } },
                                },
                            },
                        },
                    },
                },
            },
        },
    });

    private async Task<(List<Alert> Alerts, List<DispatchPause> Pauses)> StateAsync()
    {
        await using var db = database.CreateContext();
        return (await db.Alerts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(),
                await db.DispatchPauses.AsNoTracking().OrderBy(p => p.Id).ToListAsync());
    }

    private async Task<Dictionary<DispatchStatus, int>> JobStatesAsync()
    {
        await using var db = database.CreateContext();
        return await db.DispatchJobs.GroupBy(j => j.DispatchStatus).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
    }

    // ---------- Un caso por código (verificación del plan) ----------

    [Theory]
    [InlineData(190, AlertType.Token, "global")]
    [InlineData(131042, AlertType.Facturacion, "global")]
    [InlineData(132015, AlertType.PlantillaPausada, "promo_prueba:es")]
    public async Task Error_al_enviar_alerta_pausa_y_devuelve_el_lote_a_la_cola(int code, AlertType type, string key)
    {
        await SeedJobsAsync(3);
        var handler = Rejecting(code);

        var round = await DispatchAsync(handler);
        var next = await DispatchAsync(Accepting());

        Assert.Single(handler.Requests); // Tras el primer error no se intenta ningún otro del lote.
        Assert.True(round.Paused);
        Assert.True(next.Paused);
        Assert.Equal(0, next.Claimed);
        Assert.Equal(3, (await JobStatesAsync())[DispatchStatus.Queued]); // Nadie queda Claimed ni Failed.
        var (alerts, pauses) = await StateAsync();
        var alert = Assert.Single(alerts);
        Assert.Equal(type, alert.Type);
        Assert.Equal(key, alert.Key);
        Assert.Equal("Despachador", alert.Source);
        Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), alert.Message, StringComparison.Ordinal);
        var pause = Assert.Single(pauses);
        Assert.Equal(PauseScopes.Global, pause.Scope);
        Assert.Equal(type, pause.Reason);
        Assert.Equal(alert.Id, pause.AlertId);
        Assert.Null(pause.LiftedUtc);
    }

    [Fact]
    public async Task Plantilla_PAUSED_por_webhook_alerta_y_pausa()
    {
        await SeedJobsAsync(2);

        await ProcessInboxAsync(TemplatePausedWebhook());
        var handler = Accepting();
        var round = await DispatchAsync(handler);

        Assert.True(round.Paused);
        Assert.Empty(handler.Requests);
        var (alerts, pauses) = await StateAsync();
        var alert = Assert.Single(alerts);
        Assert.Equal(AlertType.PlantillaPausada, alert.Type);
        Assert.Equal("promo_prueba:es", alert.Key);
        Assert.Equal("Webhook", alert.Source);
        Assert.Contains("FIRST_PAUSE", alert.Message, StringComparison.Ordinal);
        Assert.Equal(AlertType.PlantillaPausada, Assert.Single(pauses).Reason);
        await using var db = database.CreateContext();
        Assert.Equal(WebhookInboxStatus.Procesado, (await db.WebhookInbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task Facturacion_131042_en_un_estado_failed_alerta_y_pausa()
    {
        await SeedJobsAsync(1);

        await ProcessInboxAsync(FailedStatusWebhook(131042));
        var handler = Accepting();
        var round = await DispatchAsync(handler);

        Assert.True(round.Paused);
        Assert.Empty(handler.Requests);
        var (alerts, pauses) = await StateAsync();
        Assert.Equal(AlertType.Facturacion, Assert.Single(alerts).Type);
        Assert.Single(pauses);
    }

    // ---------- Comportamiento de la pausa ----------

    [Fact]
    public async Task Reanudar_levanta_la_pausa_y_el_trabajo_devuelto_sale()
    {
        await SeedJobsAsync(2);
        await DispatchAsync(Rejecting(190));

        await using (var db = database.CreateContext())
        {
            var (pauses, alerts) = await new AlertService(db, _clock, NullLogger<AlertService>.Instance).ResumeAsync("andres", CancellationToken.None);
            Assert.Equal(1, pauses);
            Assert.Equal(1, alerts);
        }

        var handler = Accepting();
        var round = await DispatchAsync(handler);

        Assert.False(round.Paused);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, (await JobStatesAsync())[DispatchStatus.Submitted]);
        var (alertRows, pauseRows) = await StateAsync();
        Assert.Equal("andres", Assert.Single(pauseRows).LiftedBy);
        Assert.Equal("andres", Assert.Single(alertRows).ResolvedBy);

        // F1.3: reanudar queda en AuditLog (misma transacción que levantar la pausa).
        await using var check = database.CreateContext();
        var audit = Assert.Single(await check.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(("andres", "ReanudarDespacho", "DispatchPause"), (audit.User, audit.Action, audit.Entity));
        Assert.Contains("\"pausasActivas\":1", audit.Before, StringComparison.Ordinal);
        Assert.Contains("\"pausasActivas\":0", audit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reanudar_sin_pausa_no_audita()
    {
        await using var db = database.CreateContext();
        var (pauses, _) = await new AlertService(db, _clock, NullLogger<AlertService>.Instance).ResumeAsync("andres", CancellationToken.None);

        Assert.Equal(0, pauses);
        Assert.Empty(await db.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task La_misma_alerta_repetida_suma_ocurrencias_sin_duplicar_alerta_ni_pausa()
    {
        await ProcessInboxAsync(TemplatePausedWebhook(), TemplatePausedWebhook(), FailedStatusWebhook(131042));

        var (alerts, pauses) = await StateAsync();
        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Single(a => a.Type == AlertType.PlantillaPausada).Occurrences);
        Assert.Equal(1, alerts.Single(a => a.Type == AlertType.Facturacion).Occurrences);
        Assert.Equal(AlertType.PlantillaPausada, Assert.Single(pauses).Reason); // La primera causa queda como motivo.
    }

    [Fact]
    public async Task Alertas_simultaneas_dejan_una_sola_alerta_abierta_y_una_sola_pausa()
    {
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await using var db = database.CreateContext();
            await new AlertService(db, _clock, NullLogger<AlertService>.Instance)
                .RaiseAsync(AlertType.Token, "global", "Error 190", null, "Despachador", CancellationToken.None);
        })));

        var (alerts, pauses) = await StateAsync();
        Assert.Equal(10, Assert.Single(alerts).Occurrences);
        Assert.Single(pauses);
    }

    [Fact]
    public async Task Un_error_del_destinatario_no_alerta_ni_pausa()
    {
        await SeedJobsAsync(2);
        var handler = Rejecting(131026);

        var round = await DispatchAsync(handler);

        Assert.False(round.Paused);
        Assert.Equal(2, handler.Requests.Count);
        var (alerts, pauses) = await StateAsync();
        Assert.Empty(alerts);
        Assert.Empty(pauses);
    }
}
