using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Tests.Messaging;

/// <summary>
/// Procesador de WebhookInbox (F0.7). Los payloads siguen la referencia oficial de statuses:
/// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status
/// </summary>
public sealed class WebhookInboxProcessorTests
{
    private const string WabaId = "2088365445132575";
    private static readonly DateTime Start = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<WaBulkDbContext> _dbOptions = new DbContextOptionsBuilder<WaBulkDbContext>()
        .UseInMemoryDatabase($"inbox-{Guid.NewGuid()}")
        .Options;

    private readonly ManualTimeProvider _clock = new(Start);
    private readonly InboxProcessorOptions _options = new() { MaxAttempts = 3, RetryDelaySeconds = 30, MaxRetryDelaySeconds = 600 };

    // ---------- Payloads (forma oficial) ----------

    private sealed record StatusSpec(string Wamid, string Status, Guid? AttemptId = null, long Timestamp = 1763380800, int? ErrorCode = null);

    private static string Payload(params StatusSpec[] statuses) => PayloadWithEntries([statuses]);

    /// <summary>Un entry por grupo: simula un lote de Meta con varias entry/changes.</summary>
    private static string PayloadWithEntries(IEnumerable<StatusSpec[]> groups) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = groups.Select(group => new
        {
            id = WabaId,
            changes = new[]
            {
                new
                {
                    value = new
                    {
                        messaging_product = "whatsapp",
                        metadata = new { display_phone_number = "15551892694", phone_number_id = "1524289747428097" },
                        statuses = group.Select(StatusObject).ToArray(),
                    },
                    field = "messages",
                },
            },
        }).ToArray(),
    });

    private static Dictionary<string, object> StatusObject(StatusSpec s)
    {
        var o = new Dictionary<string, object>
        {
            ["id"] = s.Wamid,
            ["status"] = s.Status,
            ["timestamp"] = s.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["recipient_id"] = "59170000000",
        };
        if (s.AttemptId is { } id)
        {
            o["biz_opaque_callback_data"] = id.ToString();
        }

        if (s.Status == "sent")
        {
            o["pricing"] = new { billable = true, pricing_model = "PMP", type = "regular", category = "marketing" };
        }

        if (s.ErrorCode is { } code)
        {
            o["errors"] = new[]
            {
                new
                {
                    code,
                    title = "Message undeliverable.",
                    message = "Message undeliverable.",
                    error_data = new { details = "Message Undeliverable." },
                    href = "https://developers.facebook.com/documentation/business-messaging/whatsapp/support/error-codes",
                },
            };
        }

        return o;
    }

    // ---------- Ayudantes ----------

    private WaBulkDbContext Db() => new(_dbOptions);

    private WebhookInboxProcessor Processor(WaBulkDbContext db) =>
        new(db, Options.Create(_options), _clock, NullLogger<WebhookInboxProcessor>.Instance);

    private async Task<SendAttempt> SeedAttemptAsync(string? wamid, SendAttemptStatus status = SendAttemptStatus.Accepted)
    {
        await using var db = Db();
        var job = new DispatchJob
        {
            CampaignVersionId = 1,
            ContactId = Random.Shared.NextInt64(1, long.MaxValue),
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
            Status = status,
            Wamid = wamid,
        };
        db.SendAttempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt;
    }

    private async Task<long> InboxAsync(string body)
    {
        await using var db = Db();
        var row = new WebhookInbox { ReceivedUtc = _clock.GetUtcNow().UtcDateTime, Body = body };
        db.WebhookInbox.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task<int> RunAsync()
    {
        await using var db = Db();
        return await Processor(db).ProcessBatchAsync(CancellationToken.None);
    }

    private async Task<WebhookInbox> RowAsync(long id)
    {
        await using var db = Db();
        return await db.WebhookInbox.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<SendAttempt> AttemptAsync(Guid id)
    {
        await using var db = Db();
        return await db.SendAttempts.AsNoTracking().SingleAsync(x => x.AttemptId == id);
    }

    private async Task<List<MessageEvent>> EventsAsync(string wamid)
    {
        await using var db = Db();
        return await db.MessageEvents.AsNoTracking().Where(e => e.Wamid == wamid).OrderBy(e => e.Status).ToListAsync();
    }

    // ---------- Pruebas ----------

    [Fact]
    public async Task Sequence_SentDeliveredRead_StoresThreeEvents_AndEndsInRead()
    {
        var attempt = await SeedAttemptAsync("wamid.SEQ");
        var a = attempt.AttemptId;
        var rows = new[]
        {
            await InboxAsync(Payload(new StatusSpec("wamid.SEQ", "sent", a, 1763380800))),
            await InboxAsync(Payload(new StatusSpec("wamid.SEQ", "delivered", a, 1763380802))),
            await InboxAsync(Payload(new StatusSpec("wamid.SEQ", "read", a, 1763380900))),
        };

        Assert.Equal(3, await RunAsync());

        var events = await EventsAsync("wamid.SEQ");
        Assert.Equal([DeliveryStatus.Sent, DeliveryStatus.Delivered, DeliveryStatus.Read], events.Select(e => e.Status));
        Assert.All(events, e => Assert.Equal(a, e.AttemptId));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763380900).UtcDateTime, events[2].MetaTimestampUtc);
        Assert.Contains("\"pricing_model\":\"PMP\"", events[0].Pricing, StringComparison.Ordinal);
        Assert.Equal(DeliveryStatus.Read, (await AttemptAsync(a)).DeliveryStatus);
        foreach (var id in rows)
        {
            var row = await RowAsync(id);
            Assert.Equal(WebhookInboxStatus.Procesado, row.Status);
            Assert.Equal(1, row.Attempts);
        }
    }

    [Fact]
    public async Task DuplicateEvent_InTwoWebhooks_IsStoredOnce()
    {
        var attempt = await SeedAttemptAsync("wamid.DUP");
        var body = Payload(new StatusSpec("wamid.DUP", "delivered", attempt.AttemptId));
        var first = await InboxAsync(body);
        var second = await InboxAsync(body); // Meta reintenta el mismo webhook.

        await RunAsync();

        Assert.Single(await EventsAsync("wamid.DUP"));
        Assert.Equal(WebhookInboxStatus.Procesado, (await RowAsync(first)).Status);
        Assert.Equal(WebhookInboxStatus.Procesado, (await RowAsync(second)).Status);
    }

    [Fact]
    public async Task DuplicateEvent_InsideTheSameBatch_IsStoredOnce()
    {
        var attempt = await SeedAttemptAsync("wamid.DUP2");
        var s = new StatusSpec("wamid.DUP2", "sent", attempt.AttemptId);
        await InboxAsync(Payload(s, s));

        await RunAsync();

        Assert.Single(await EventsAsync("wamid.DUP2"));
    }

    [Fact]
    public async Task ReadBeforeDelivered_DoesNotGoBack()
    {
        var attempt = await SeedAttemptAsync("wamid.ORD");
        await InboxAsync(Payload(new StatusSpec("wamid.ORD", "read", attempt.AttemptId)));
        await InboxAsync(Payload(new StatusSpec("wamid.ORD", "delivered", attempt.AttemptId)));

        await RunAsync();

        Assert.Equal(DeliveryStatus.Read, (await AttemptAsync(attempt.AttemptId)).DeliveryStatus);
        Assert.Equal(2, (await EventsAsync("wamid.ORD")).Count); // Ambos eventos quedan en el historial.
    }

    [Fact]
    public async Task DeliveredBeforeSent_LikeTheRealCase_EndsInDelivered()
    {
        // Caso real de F0.6 (D-021): WebhookInbox Id=3 delivered llegó antes que Id=4 sent.
        var attempt = await SeedAttemptAsync("wamid.REAL");
        await InboxAsync(Payload(new StatusSpec("wamid.REAL", "delivered", attempt.AttemptId)));
        await InboxAsync(Payload(new StatusSpec("wamid.REAL", "sent", attempt.AttemptId)));

        await RunAsync();

        Assert.Equal(DeliveryStatus.Delivered, (await AttemptAsync(attempt.AttemptId)).DeliveryStatus);
        Assert.Equal(2, (await EventsAsync("wamid.REAL")).Count);
    }

    [Fact]
    public async Task WebhookBeforeWamidIsSaved_CorrelatesByAttemptId()
    {
        // El despachador aún no guardó la respuesta del POST: intento en Submitting y sin wamid.
        var attempt = await SeedAttemptAsync(wamid: null, SendAttemptStatus.Submitting);
        var row = await InboxAsync(Payload(new StatusSpec("wamid.EARLY", "sent", attempt.AttemptId)));

        await RunAsync();

        var ev = Assert.Single(await EventsAsync("wamid.EARLY"));
        Assert.Equal(attempt.AttemptId, ev.AttemptId);
        var updated = await AttemptAsync(attempt.AttemptId);
        Assert.Equal("wamid.EARLY", updated.Wamid);
        Assert.Equal(SendAttemptStatus.Submitting, updated.Status); // El estado del intento lo decide el despachador.
        Assert.Equal(DeliveryStatus.Sent, updated.DeliveryStatus);
        Assert.Equal(WebhookInboxStatus.Procesado, (await RowAsync(row)).Status);
    }

    [Fact]
    public async Task SubmissionUnknown_IsResolvedAsAccepted_WhenAWebhookArrives()
    {
        var attempt = await SeedAttemptAsync(wamid: null, SendAttemptStatus.SubmissionUnknown);
        await InboxAsync(Payload(new StatusSpec("wamid.UNK", "delivered", attempt.AttemptId)));

        await RunAsync();

        var updated = await AttemptAsync(attempt.AttemptId);
        Assert.Equal(SendAttemptStatus.Accepted, updated.Status);
        Assert.Equal("wamid.UNK", updated.Wamid);
        Assert.Equal(DeliveryStatus.Delivered, updated.DeliveryStatus);
    }

    [Fact]
    public async Task WithoutCallbackData_CorrelatesByWamid()
    {
        var attempt = await SeedAttemptAsync("wamid.BYWAMID");
        await InboxAsync(Payload(new StatusSpec("wamid.BYWAMID", "failed", AttemptId: null, ErrorCode: 131026)));

        await RunAsync();

        var ev = Assert.Single(await EventsAsync("wamid.BYWAMID"));
        Assert.Equal(attempt.AttemptId, ev.AttemptId);
        Assert.Equal(DeliveryStatus.Failed, ev.Status);
        Assert.Contains("131026", ev.Errors, StringComparison.Ordinal);
        Assert.Equal(DeliveryStatus.Failed, (await AttemptAsync(attempt.AttemptId)).DeliveryStatus);
    }

    [Fact]
    public async Task Batch_WithSeveralEntriesAndStatuses_IsSplitAndApplied()
    {
        var a1 = await SeedAttemptAsync("wamid.B1");
        var a2 = await SeedAttemptAsync("wamid.B2");
        var row = await InboxAsync(PayloadWithEntries(
        [
            [new StatusSpec("wamid.B1", "sent", a1.AttemptId), new StatusSpec("wamid.B2", "sent", a2.AttemptId)],
            [new StatusSpec("wamid.B1", "delivered", a1.AttemptId)],
        ]));

        await RunAsync();

        Assert.Equal(2, (await EventsAsync("wamid.B1")).Count);
        Assert.Single(await EventsAsync("wamid.B2"));
        Assert.Equal(DeliveryStatus.Delivered, (await AttemptAsync(a1.AttemptId)).DeliveryStatus);
        Assert.Equal(DeliveryStatus.Sent, (await AttemptAsync(a2.AttemptId)).DeliveryStatus);
        Assert.Equal(WebhookInboxStatus.Procesado, (await RowAsync(row)).Status);
    }

    [Fact]
    public async Task UnknownWamid_GoesToEnEspera_ThenProcessedWhenTheAttemptAppears()
    {
        var row = await InboxAsync(Payload(new StatusSpec("wamid.LATE", "sent")));

        await RunAsync();

        var waiting = await RowAsync(row);
        Assert.Equal(WebhookInboxStatus.EnEspera, waiting.Status);
        Assert.Equal(1, waiting.Attempts);
        Assert.Equal(Start.AddSeconds(30), waiting.NextAttemptUtc);
        Assert.Empty(await EventsAsync("wamid.LATE"));

        // Antes de que venza la espera no se vuelve a tomar.
        Assert.Equal(0, await RunAsync());

        await SeedAttemptAsync("wamid.LATE");
        _clock.Advance(TimeSpan.FromSeconds(31));
        await RunAsync();

        var done = await RowAsync(row);
        Assert.Equal(WebhookInboxStatus.Procesado, done.Status);
        Assert.Equal(2, done.Attempts);
        Assert.Null(done.Error);
        Assert.Single(await EventsAsync("wamid.LATE"));
    }

    [Fact]
    public async Task UnknownWamid_AfterMaxAttempts_GoesToRevision()
    {
        var row = await InboxAsync(Payload(new StatusSpec("wamid.NEVER", "delivered")));

        for (var i = 0; i < _options.MaxAttempts; i++)
        {
            await RunAsync();
            _clock.Advance(TimeSpan.FromMinutes(15));
        }

        var review = await RowAsync(row);
        Assert.Equal(WebhookInboxStatus.Revision, review.Status);
        Assert.Equal(_options.MaxAttempts, review.Attempts);
        Assert.Contains("wamid.NEVER", review.Error, StringComparison.Ordinal);
        Assert.Equal(0, await RunAsync()); // Revision no se vuelve a tomar.
    }

    [Fact]
    public async Task InvalidJson_GoesToRevision()
    {
        var row = await InboxAsync("{no es json");

        await RunAsync();

        Assert.Equal(WebhookInboxStatus.Revision, (await RowAsync(row)).Status);
    }

    [Fact]
    public async Task FieldsWithoutStatuses_AreProcessedWithoutEvents()
    {
        // account_update (como WebhookInbox Id=1) y un mensaje entrante (como el de prueba del panel, Id=2).
        var account = await InboxAsync($$"""{"object":"whatsapp_business_account","entry":[{"id":"{{WabaId}}","time":1763380800,"changes":[{"value":{"event":"VERIFIED_ACCOUNT"},"field":"account_update"}]}]}""");
        var inbound = await InboxAsync("""{"object":"whatsapp_business_account","entry":[{"id":"0","changes":[{"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"display_phone_number":"16505551111","phone_number_id":"123456123"},"contacts":[{"profile":{"name":"test user name"},"wa_id":"16315551181"}],"messages":[{"from":"16315551181","id":"ABGGFlA5Fpa","timestamp":"1504902988","type":"text","text":{"body":"this is a text message"}}]}}]}]}""");

        await RunAsync();

        Assert.Equal(WebhookInboxStatus.Procesado, (await RowAsync(account)).Status);
        Assert.Equal(WebhookInboxStatus.Procesado, (await RowAsync(inbound)).Status);
        await using var db = Db();
        Assert.Empty(await db.MessageEvents.ToListAsync());
    }

    [Fact]
    public async Task WamidMismatch_ForTheSameAttempt_GoesToRevision()
    {
        var attempt = await SeedAttemptAsync("wamid.ORIGINAL");
        var row = await InboxAsync(Payload(new StatusSpec("wamid.OTHER", "sent", attempt.AttemptId)));

        await RunAsync();

        Assert.Equal(WebhookInboxStatus.Revision, (await RowAsync(row)).Status);
        Assert.Empty(await EventsAsync("wamid.OTHER"));
    }

    private sealed class ManualTimeProvider(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
