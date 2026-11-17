using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Tests.Meta;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Consent;

/// <summary>
/// Bajas (F0.10) contra SQL Server. Verificación del plan: tras cada tipo de baja, el siguiente trabajo de ese
/// contacto queda Skipped sin POST (el ya encolado, al enviar; uno nuevo, al encolar); un resume no levanta una baja
/// de la empresa. Payloads con la forma de la referencia de Meta (messages/text, messages/button, user_preferences).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OptOutIntegrationTests(SqlServerTestDatabase database)
    : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private const string Phone = "+59171111111";
    private const string WaId = "59171111111";
    private static readonly DateTime Start = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long StartUnix = new DateTimeOffset(Start).ToUnixTimeSeconds();

    private readonly ManualClock _clock = new(Start);
    private int _wamidCounter;

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Payloads ----------

    private static string Envelope(string field, object value) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[] { new { id = "2088365445132575", changes = new[] { new { field, value } } } },
    });

    private static readonly object Metadata = new { display_phone_number = "15551892694", phone_number_id = "1524289747428097" };

    private static Dictionary<string, object> Inbound(string id, string type, long offset, bool withPhone = true)
    {
        var m = new Dictionary<string, object>
        {
            ["from_user_id"] = "BO.123",
            ["id"] = id,
            ["timestamp"] = (StartUnix + offset).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["type"] = type,
        };
        if (withPhone)
        {
            m["from"] = WaId;
        }

        return m;
    }

    private static object Contacts(bool withPhone = true) => new[]
    {
        withPhone
            ? new Dictionary<string, object> { ["profile"] = new { name = "Cliente" }, ["wa_id"] = WaId, ["user_id"] = "BO.123" }
            : new Dictionary<string, object> { ["profile"] = new { name = "Cliente" }, ["user_id"] = "BO.123" },
    };

    private static string MessagesValue(Dictionary<string, object> message, bool withPhone = true) =>
        Envelope("messages", new
        {
            messaging_product = "whatsapp",
            metadata = Metadata,
            contacts = Contacts(withPhone),
            messages = new[] { message },
        });

    private static string TextMessage(string body, string id = "wamid.IN1", long offset = 10, bool withPhone = true)
    {
        var m = Inbound(id, "text", offset, withPhone);
        m["text"] = new { body };
        return MessagesValue(m, withPhone);
    }

    private static string ButtonMessage(string payload, string text, string id = "wamid.IN2")
    {
        var m = Inbound(id, "button", 10);
        m["context"] = new { from = "15551892694", id = "wamid.PROMO" };
        m["button"] = new { payload, text };
        return MessagesValue(m);
    }

    private static string InteractiveReply(string replyId, string title)
    {
        var m = Inbound("wamid.IN3", "interactive", 10);
        m["interactive"] = new { type = "button_reply", button_reply = new { id = replyId, title } };
        return MessagesValue(m);
    }

    private static string UserPreference(string value, long offset) =>
        Envelope("user_preferences", new
        {
            messaging_product = "whatsapp",
            metadata = Metadata,
            contacts = new[] { new { wa_id = WaId, profile = new { name = "Cliente" } } },
            user_preferences = new[]
            {
                new
                {
                    wa_id = WaId,
                    detail = $"User requested to {value} marketing messages",
                    category = "marketing_messages",
                    value,
                    timestamp = StartUnix + offset, // Entero, como en la referencia.
                },
            },
        });

    private static string FailedStatus131050() =>
        Envelope("messages", new
        {
            messaging_product = "whatsapp",
            metadata = Metadata,
            statuses = new[]
            {
                new
                {
                    id = "wamid.OUT1",
                    status = "failed",
                    timestamp = (StartUnix + 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    recipient_id = WaId,
                    errors = new[]
                    {
                        new
                        {
                            code = 131050,
                            title = "Unable to deliver the message. This recipient has chosen to stop receiving marketing messages on WhatsApp from your business.",
                        },
                    },
                },
            },
        });

    // ---------- Ayudantes ----------

    private async Task SeedOptInAsync()
    {
        await using var db = database.CreateContext();
        db.ConsentRecords.Add(new ConsentRecord
        {
            PhoneE164 = Phone, Status = ConsentStatus.OptIn, Source = ConsentSource.Manual,
            RecordedUtc = Start.AddDays(-30), RecordedBy = "prueba",
        });
        await db.SaveChangesAsync();
    }

    private async Task<DispatchJob> EnqueueAsync()
    {
        await using var db = database.CreateContext();
        return await new EnqueueService(db, new ConsentService(db, _clock)).EnqueueAsync(
            new EnqueueRequest(await TestServices.CampaignVersionAsync(db), await TestServices.ContactIdAsync(db, Phone), Phone, Start, Start.AddDays(1)), CancellationToken.None);
    }

    private async Task ProcessInboxAsync(params string[] bodies)
    {
        await using var db = database.CreateContext();
        foreach (var body in bodies)
        {
            db.WebhookInbox.Add(new WebhookInbox { ReceivedUtc = Start, Body = body });
        }

        await db.SaveChangesAsync();
        var processor = TestServices.Processor(db, _clock);
        await processor.ProcessBatchAsync(CancellationToken.None);
    }

    private FakeHttpHandler Accepting() => new((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
        $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.T{{Interlocked.Increment(ref _wamidCounter)}}","message_status":"accepted"}]}""")));

    private static FakeHttpHandler Rejecting131050() => new((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.BadRequest,
        """{"error":{"message":"Unable to deliver the message.","type":"OAuthException","code":131050,"error_data":{"details":"x"},"fbtrace_id":"T"}}""")));

    private async Task<DispatchRound> DispatchAsync(FakeHttpHandler handler)
    {
        await using var db = database.CreateContext();
        var options = Options.Create(new DispatcherOptions { WorkerId = "W", LeaseSeconds = 120 });
        var dispatcher = new Dispatcher(db, new DispatchQueue(db), new ConsentService(db, _clock), TestCapacity.Unlimited(db), new AlertService(db, _clock, NullLogger<AlertService>.Instance), TestServices.Lifecycle(db), TestServices.RateLimiter, TestServices.Frequency(),
            MetaWhatsAppClientTests.CreateClient(handler), options, new DispatcherIdentity(options), _clock,
            NullLogger<Dispatcher>.Instance);
        return await dispatcher.RunOnceAsync(CancellationToken.None);
    }

    private async Task<DispatchJob> JobAsync(long id)
    {
        await using var db = database.CreateContext();
        return await db.DispatchJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    private async Task<int> AttemptCountAsync(long jobId)
    {
        await using var db = database.CreateContext();
        return await db.SendAttempts.CountAsync(a => a.DispatchJobId == jobId);
    }

    // ---------- Verificación del plan: cada tipo de baja ----------

    [Theory]
    [InlineData("palabra-clave", SkipReasons.BajaEmpresa)]
    [InlineData("boton-plantilla", SkipReasons.BajaEmpresa)]
    [InlineData("boton-interactivo", SkipReasons.BajaEmpresa)]
    [InlineData("intranet", SkipReasons.BajaEmpresa)]
    [InlineData("user-preferences-stop", SkipReasons.BajaPlataforma)]
    [InlineData("error-131050-webhook", SkipReasons.BajaPlataforma)]
    [InlineData("error-131050-envio", SkipReasons.BajaPlataforma)]
    public async Task Tras_cada_baja_el_siguiente_trabajo_queda_Skipped_sin_POST(string kind, string reason)
    {
        await SeedOptInAsync();
        var queuedBefore = await EnqueueAsync(); // Encolado antes de la baja: se revisa al enviar.
        Assert.Equal(DispatchStatus.Queued, queuedBefore.DispatchStatus);

        switch (kind)
        {
            case "palabra-clave": await ProcessInboxAsync(TextMessage("Baja")); break;
            case "boton-plantilla": await ProcessInboxAsync(ButtonMessage("BAJA_PROMOCIONES", "No recibir más")); break;
            case "boton-interactivo": await ProcessInboxAsync(InteractiveReply("x", "No recibir más")); break;
            case "user-preferences-stop": await ProcessInboxAsync(UserPreference("stop", 10)); break;
            case "error-131050-webhook": await ProcessInboxAsync(FailedStatus131050()); break;
            case "intranet":
                await using (var db = database.CreateContext())
                {
                    Assert.True(await new ConsentService(db, _clock).RecordCompanyAsync(
                        Phone, ConsentStatus.OptOut, ConsentSource.Intranet, "Pedido por teléfono, ticket 123", "andres", CancellationToken.None));
                }

                break;
            case "error-131050-envio":
                // Un envío anterior de otra campaña recibe 131050: queda la baja de plataforma.
                var earlier = await EnqueueAsync();
                await using (var db = database.CreateContext())
                {
                    await db.DispatchJobs.Where(j => j.Id == queuedBefore.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.ScheduledUtc, Start.AddMinutes(1)));
                }

                var rejecting = Rejecting131050();
                await DispatchAsync(rejecting);
                Assert.Single(rejecting.Requests);
                Assert.Equal(DispatchStatus.Failed, (await JobAsync(earlier.Id)).DispatchStatus);
                _clock.Advance(TimeSpan.FromMinutes(2));
                break;
        }

        var handler = Accepting();
        var round = await DispatchAsync(handler);

        Assert.Empty(handler.Requests);
        Assert.Equal(1, round.Skipped);
        var skipped = await JobAsync(queuedBefore.Id);
        Assert.Equal(DispatchStatus.Skipped, skipped.DispatchStatus);
        Assert.Equal(reason, skipped.SkipReason);
        Assert.Null(skipped.LockedBy);
        Assert.Equal(0, await AttemptCountAsync(queuedBefore.Id));

        var enqueuedAfter = await EnqueueAsync(); // Encolado después de la baja: se revisa al encolar.
        Assert.Equal(DispatchStatus.Skipped, enqueuedAfter.DispatchStatus);
        Assert.Equal(reason, enqueuedAfter.SkipReason);
    }

    [Fact]
    public async Task Un_resume_no_levanta_una_baja_de_la_empresa()
    {
        await SeedOptInAsync();
        await ProcessInboxAsync(TextMessage("STOP"), UserPreference("stop", 20), UserPreference("resume", 30));

        var job = await EnqueueAsync();
        var handler = Accepting();
        await DispatchAsync(handler);

        Assert.Equal(DispatchStatus.Skipped, job.DispatchStatus);
        Assert.Equal(SkipReasons.BajaEmpresa, job.SkipReason);
        Assert.Empty(handler.Requests);
        await using var db = database.CreateContext();
        Assert.Equal(ConsentStatus.OptOut, (await db.ConsentRecords.OrderByDescending(c => c.RecordedUtc).FirstAsync()).Status);
    }

    [Fact]
    public async Task Un_resume_si_levanta_un_stop_de_plataforma()
    {
        await SeedOptInAsync();
        await ProcessInboxAsync(UserPreference("stop", 20), UserPreference("resume", 30));

        var job = await EnqueueAsync();
        var handler = Accepting();
        await DispatchAsync(handler);

        Assert.Equal(DispatchStatus.Queued, job.DispatchStatus);
        Assert.Equal(DispatchStatus.Submitted, (await JobAsync(job.Id)).DispatchStatus);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Preferencias_en_desorden_vale_la_de_hora_de_Meta_mas_reciente()
    {
        await SeedOptInAsync();
        // Llega primero el stop (t+30) y después un resume más viejo (t+20): sigue en stop.
        await ProcessInboxAsync(UserPreference("stop", 30));
        await ProcessInboxAsync(UserPreference("resume", 20));

        var job = await EnqueueAsync();

        Assert.Equal(DispatchStatus.Skipped, job.DispatchStatus);
        Assert.Equal(SkipReasons.BajaPlataforma, job.SkipReason);
    }

    [Fact]
    public async Task Sin_consentimiento_no_se_envia()
    {
        var job = await EnqueueAsync();

        Assert.Equal(DispatchStatus.Skipped, job.DispatchStatus);
        Assert.Equal(SkipReasons.SinConsentimiento, job.SkipReason);
    }

    [Fact]
    public async Task El_mismo_webhook_dos_veces_registra_una_sola_baja()
    {
        await ProcessInboxAsync(TextMessage("BAJA"), TextMessage("BAJA"), UserPreference("stop", 10), UserPreference("stop", 10));

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.ConsentRecords.CountAsync());
        Assert.Equal(1, await db.PlatformPreferences.CountAsync());
        Assert.All(await db.WebhookInbox.ToListAsync(), r => Assert.Equal(WebhookInboxStatus.Procesado, r.Status));
        var record = await db.ConsentRecords.SingleAsync();
        Assert.Equal(ConsentSource.PalabraClave, record.Source);
        Assert.Equal("wamid.IN1", record.SourceWamid);
        Assert.Equal(Phone, record.PhoneE164);
        Assert.Equal(Start.AddSeconds(10), record.RecordedUtc);
    }

    [Fact]
    public async Task Un_mensaje_que_no_es_baja_no_cambia_nada()
    {
        await SeedOptInAsync();
        await ProcessInboxAsync(TextMessage("no quiero salir de la lista, gracias"), ButtonMessage("ME_INTERESA", "Me interesa"));

        Assert.Equal(DispatchStatus.Queued, (await EnqueueAsync()).DispatchStatus);
    }

    [Fact]
    public async Task Baja_sin_telefono_va_a_Revision()
    {
        await ProcessInboxAsync(TextMessage("BAJA", withPhone: false));

        await using var db = database.CreateContext();
        var row = await db.WebhookInbox.SingleAsync();
        Assert.Equal(WebhookInboxStatus.Revision, row.Status);
        Assert.Contains("BO.123", row.Error, StringComparison.Ordinal);
        Assert.Equal(0, await db.ConsentRecords.CountAsync());
    }

    [Fact]
    public async Task Una_importacion_no_levanta_la_baja_y_un_opt_in_manual_si()
    {
        await SeedOptInAsync();
        await ProcessInboxAsync(TextMessage("SALIR"));
        _clock.Advance(TimeSpan.FromMinutes(1));

        await using (var db = database.CreateContext())
        {
            var consent = new ConsentService(db, _clock);
            Assert.False(await consent.RecordCompanyAsync(Phone, ConsentStatus.OptIn, ConsentSource.Importacion, "lista.xlsx", "importador", CancellationToken.None));
            Assert.Equal(SkipReasons.BajaEmpresa, (await consent.EvaluateAsync(Phone, CancellationToken.None)).SkipReason);

            Assert.True(await consent.RecordCompanyAsync(Phone, ConsentStatus.OptIn, ConsentSource.Manual, "Nuevo opt-in firmado", "andres", CancellationToken.None));
            Assert.True((await consent.EvaluateAsync(Phone, CancellationToken.None)).Eligible);
        }
    }
}
