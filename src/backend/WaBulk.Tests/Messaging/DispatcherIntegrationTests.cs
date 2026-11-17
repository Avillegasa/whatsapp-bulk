using System.Net;
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

namespace WaBulk.Tests.Messaging;

/// <summary>
/// Despachador con cola y lease (F0.9) contra SQL Server real. Escenarios del plan: caída antes del POST → el
/// trabajo vuelve a la cola; caída después del POST → SubmissionUnknown/NeedsReview y el handler falso registra
/// un solo POST. Más: reclamo con READPAST, dueño del lease, reintentos (regla 9) y vencimiento.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DispatcherIntegrationTests(SqlServerTestDatabase database)
    : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly ManualClock _clock = new(Start);
    private int _wamidCounter;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        // Desde F0.10 solo se envía a quien tiene OptIn vigente: el contacto de estas pruebas lo tiene.
        await using var db = database.CreateContext();
        db.ConsentRecords.Add(new ConsentRecord
        {
            PhoneE164 = "+59170000000", Status = ConsentStatus.OptIn, Source = ConsentSource.Manual,
            RecordedUtc = Start.AddDays(-1), RecordedBy = "prueba",
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Ayudantes ----------

    private static DispatcherOptions Options(string owner) => new()
    {
        Enabled = true,
        WorkerId = owner,
        BatchSize = 10,
        LeaseSeconds = 120,
        MaxAttempts = 5,
        RetryBaseSeconds = 30,
        RetryMaxSeconds = 1800,
    };

    private Dispatcher NewDispatcher(WaBulkDbContext db, FakeHttpHandler handler, string owner, TimeSpan? httpTimeout = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options(owner));
        return new Dispatcher(db, new DispatchQueue(db), new ConsentService(db, _clock), TestCapacity.Unlimited(db), new AlertService(db, _clock, NullLogger<AlertService>.Instance), TestServices.Lifecycle(db), TestServices.RateLimiter, TestServices.Frequency(),
            MetaWhatsAppClientTests.CreateClient(handler, httpTimeout),
            options, new DispatcherIdentity(options), _clock, NullLogger<Dispatcher>.Instance);
    }

    /// <summary>Meta falso que acepta con un wamid distinto por POST (Wamid tiene índice único).</summary>
    private FakeHttpHandler Accepting(Func<Task>? duringPost = null) => new(async (_, _) =>
    {
        if (duringPost is not null)
        {
            await duringPost();
        }

        var wamid = $"wamid.TEST{Interlocked.Increment(ref _wamidCounter)}";
        return MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
            $$"""{"messaging_product":"whatsapp","messages":[{"id":"{{wamid}}","message_status":"accepted"}]}""");
    });

    private static FakeHttpHandler Rejecting(int code) => new((_, _) => Task.FromResult(
        MetaWhatsAppClientTests.Json(HttpStatusCode.BadRequest,
            $$$"""{"error":{"message":"error de prueba","type":"OAuthException","code":{{{code}}},"error_data":{"details":"prueba"},"fbtrace_id":"T"}}""")));

    private async Task<long> SeedJobAsync(
        DispatchStatus status = DispatchStatus.Queued, TimeSpan? scheduledIn = null, TimeSpan? expiresIn = null,
        int priority = 0, DateTime? nextAttemptUtc = null)
    {
        await using var db = database.CreateContext();
        var job = new DispatchJob
        {
            CampaignVersionId = await TestServices.CampaignVersionAsync(db), // Una versión por trabajo: único (CampaignVersionId, ContactId).
            ContactId = await TestServices.ContactIdAsync(db, "+59170000000"),
            PhoneE164 = "+59170000000",
            ScheduledUtc = Start + (scheduledIn ?? TimeSpan.Zero),
            ExpiresUtc = Start + (expiresIn ?? TimeSpan.FromDays(1)),
            Priority = priority,
            DispatchStatus = status,
            NextAttemptUtc = nextAttemptUtc,
        };
        db.DispatchJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<DispatchJob> JobAsync(long id)
    {
        await using var db = database.CreateContext();
        return await db.DispatchJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    private async Task<List<SendAttempt>> AttemptsAsync(long jobId)
    {
        await using var db = database.CreateContext();
        return await db.SendAttempts.AsNoTracking().Where(a => a.DispatchJobId == jobId).OrderBy(a => a.StartedUtc).ToListAsync();
    }

    private async Task<DispatchRound> RunAsync(FakeHttpHandler handler, string owner = "B")
    {
        await using var db = database.CreateContext();
        return await NewDispatcher(db, handler, owner).RunOnceAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<long>> ClaimAsync(string owner, int batch = 10)
    {
        await using var db = database.CreateContext();
        return await new DispatchQueue(db).ClaimBatchAsync(owner, batch, _clock.UtcNow, TimeSpan.FromSeconds(120), CancellationToken.None);
    }

    // ---------- Reclamo ----------

    [Fact]
    public async Task Reclamo_toma_solo_los_listos_por_prioridad_y_marca_vencidos()
    {
        var ready = await SeedJobAsync();
        var urgent = await SeedJobAsync(priority: 5);
        var future = await SeedJobAsync(scheduledIn: TimeSpan.FromHours(1));
        var retryDue = await SeedJobAsync(DispatchStatus.RetryScheduled, nextAttemptUtc: Start.AddSeconds(-1));
        var retryLater = await SeedJobAsync(DispatchStatus.RetryScheduled, nextAttemptUtc: Start.AddMinutes(5));
        var overdue = await SeedJobAsync(scheduledIn: TimeSpan.FromHours(-2), expiresIn: TimeSpan.FromHours(-1));
        var submitted = await SeedJobAsync(DispatchStatus.Submitted);

        await using var db = database.CreateContext();
        var queue = new DispatchQueue(db);
        Assert.Equal(1, await queue.ExpireOverdueAsync(_clock.UtcNow, CancellationToken.None));
        var claimed = await queue.ClaimBatchAsync("A", 10, _clock.UtcNow, TimeSpan.FromSeconds(120), CancellationToken.None);

        Assert.Equal([urgent, ready, retryDue], claimed);
        var a = await JobAsync(urgent);
        Assert.Equal(DispatchStatus.Claimed, a.DispatchStatus);
        Assert.Equal("A", a.LockedBy);
        Assert.Equal(Start.AddSeconds(120), a.LeaseUntilUtc);
        Assert.Equal(DispatchStatus.Queued, (await JobAsync(future)).DispatchStatus);
        Assert.Equal(DispatchStatus.RetryScheduled, (await JobAsync(retryLater)).DispatchStatus);
        Assert.Equal(DispatchStatus.Expired, (await JobAsync(overdue)).DispatchStatus);
        Assert.Equal(DispatchStatus.Submitted, (await JobAsync(submitted)).DispatchStatus);
    }

    [Fact]
    public async Task Dos_reclamos_simultaneos_no_comparten_trabajos_y_no_se_esperan()
    {
        var ids = new List<long>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await SeedJobAsync());
        }

        // A reclama 3 dentro de una transacción que sigue abierta (sus filas quedan bloqueadas).
        await using var dbA = database.CreateContext();
        await using var txA = await dbA.Database.BeginTransactionAsync();
        var claimedA = await new DispatchQueue(dbA).ClaimBatchAsync("A", 3, _clock.UtcNow, TimeSpan.FromSeconds(120), CancellationToken.None);

        // B no espera a A (READPAST salta las filas bloqueadas): toma las otras 3.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var dbB = database.CreateContext();
        var claimedB = await new DispatchQueue(dbB).ClaimBatchAsync("B", 10, _clock.UtcNow, TimeSpan.FromSeconds(120), timeout.Token);
        await txA.CommitAsync();

        Assert.True(claimedA.Count == 3 && claimedB.Count == 3, $"A={claimedA.Count} B={claimedB.Count}");
        Assert.Empty(claimedA.Intersect(claimedB));
        Assert.Equal(ids.Order(), claimedA.Concat(claimedB).Order());
    }

    // ---------- Escenarios del plan ----------

    [Fact]
    public async Task Caida_antes_del_POST_el_trabajo_vuelve_a_la_cola_y_se_envia_una_vez()
    {
        var job = await SeedJobAsync();
        Assert.Equal([job], await ClaimAsync("A")); // A reclama y "se cae" antes de insertar el intento.

        _clock.Advance(TimeSpan.FromSeconds(121));
        var handler = Accepting();
        var round = await RunAsync(handler, "B");

        Assert.Equal(1, round.Recovered);
        Assert.Equal(1, round.Sent);
        Assert.Single(handler.Requests);
        var saved = await JobAsync(job);
        Assert.Equal(DispatchStatus.Submitted, saved.DispatchStatus);
        Assert.Null(saved.LockedBy);
        Assert.Null(saved.LeaseUntilUtc);
        Assert.Equal(1, saved.Attempts);
        Assert.Equal(SendAttemptStatus.Accepted, Assert.Single(await AttemptsAsync(job)).Status);
    }

    [Fact]
    public async Task Caida_despues_del_POST_queda_SubmissionUnknown_NeedsReview_con_un_solo_POST()
    {
        var job = await SeedJobAsync();
        var handler = Accepting();

        // A: reclama, confirma el intento y hace el POST; "se cae" antes de guardar la respuesta.
        await using (var dbA = database.CreateContext())
        {
            var queue = new DispatchQueue(dbA);
            await queue.ClaimBatchAsync("A", 10, _clock.UtcNow, TimeSpan.FromSeconds(120), CancellationToken.None);
            var attempt = new SendAttempt { AttemptId = Guid.CreateVersion7(), DispatchJobId = job, StartedUtc = _clock.UtcNow };
            Assert.Equal(BeginOutcome.Started,
                await queue.BeginAttemptAsync(job, "A", attempt, _clock.UtcNow, TimeSpan.FromSeconds(120), CancellationToken.None));
            await MetaWhatsAppClientTests.CreateClient(handler)
                .SendTemplateAsync("+59170000000", new("hello_world", "en_US"), attempt.AttemptId, CancellationToken.None);
        }

        _clock.Advance(TimeSpan.FromSeconds(121));
        var round = await RunAsync(handler, "B");
        _clock.Advance(TimeSpan.FromHours(1));
        await RunAsync(handler, "B"); // Otra vuelta: tampoco reenvía.

        Assert.Equal(1, round.Recovered);
        Assert.Equal(0, round.Claimed);
        Assert.Single(handler.Requests);
        Assert.Equal(DispatchStatus.NeedsReview, (await JobAsync(job)).DispatchStatus);
        var saved = Assert.Single(await AttemptsAsync(job));
        Assert.Equal(SendAttemptStatus.SubmissionUnknown, saved.Status);
        Assert.Contains("Lease vencido", saved.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caida_despues_del_POST_con_webhook_ya_procesado_se_recupera_como_Accepted()
    {
        var job = await SeedJobAsync();
        Guid attemptId;
        await using (var dbA = database.CreateContext())
        {
            var queue = new DispatchQueue(dbA);
            await queue.ClaimBatchAsync("A", 10, _clock.UtcNow, TimeSpan.FromSeconds(120), CancellationToken.None);
            var attempt = new SendAttempt { AttemptId = Guid.CreateVersion7(), DispatchJobId = job, StartedUtc = _clock.UtcNow };
            await queue.BeginAttemptAsync(job, "A", attempt, _clock.UtcNow, TimeSpan.FromSeconds(120), CancellationToken.None);
            attemptId = attempt.AttemptId;
        }

        // El procesador guardó el wamid de un webhook con este AttemptId (F0.7) antes de que A guardara la respuesta.
        await using (var db = database.CreateContext())
        {
            await db.SendAttempts.Where(a => a.AttemptId == attemptId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Wamid, "wamid.WEBHOOK"));
        }

        _clock.Advance(TimeSpan.FromSeconds(121));
        var handler = Accepting();
        await RunAsync(handler, "B");

        Assert.Empty(handler.Requests);
        Assert.Equal(DispatchStatus.Submitted, (await JobAsync(job)).DispatchStatus);
        Assert.Equal(SendAttemptStatus.Accepted, Assert.Single(await AttemptsAsync(job)).Status);
    }

    // ---------- Dueño del lease ----------

    [Fact]
    public async Task Sin_lease_no_se_envia()
    {
        var job = await SeedJobAsync();
        await ClaimAsync("A");
        _clock.Advance(TimeSpan.FromSeconds(121));

        var handlerB = Accepting();
        await RunAsync(handlerB, "B"); // B recupera, reclama y envía.

        var handlerA = Accepting();
        await using var dbA = database.CreateContext();
        var outcome = await NewDispatcher(dbA, handlerA, "A").ProcessJobAsync(job, CancellationToken.None);

        Assert.Equal(BeginOutcome.LeaseLost, outcome);
        Assert.Empty(handlerA.Requests);
        Assert.Single(handlerB.Requests);
        Assert.Single(await AttemptsAsync(job));
    }

    [Fact]
    public async Task Lease_vencido_durante_el_POST_el_resultado_tardio_se_guarda_y_el_trabajo_queda_Submitted()
    {
        var job = await SeedJobAsync();
        await ClaimAsync("A");

        // Durante el POST de A (sin transacción abierta, regla 5), el lease vence y B ejecuta la recuperación.
        // Si A tuviera la fila bloqueada, READPAST la saltaría y Recovered sería 0.
        var recoveredDuringPost = -1;
        var handler = Accepting(async () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(121));
            await using var dbB = database.CreateContext();
            recoveredDuringPost = (await new DispatchQueue(dbB).RecoverExpiredLeasesAsync(_clock.UtcNow, CancellationToken.None)).Count;
        });

        await using var dbA = database.CreateContext();
        var outcome = await NewDispatcher(dbA, handler, "A").ProcessJobAsync(job, CancellationToken.None);

        Assert.Equal(BeginOutcome.Started, outcome);
        Assert.Equal(1, recoveredDuringPost);
        Assert.Single(handler.Requests);
        var saved = await JobAsync(job);
        Assert.Equal(DispatchStatus.Submitted, saved.DispatchStatus); // NeedsReview → Submitted con la respuesta real.
        Assert.Null(saved.LockedBy);
        var attempt = Assert.Single(await AttemptsAsync(job));
        Assert.Equal(SendAttemptStatus.Accepted, attempt.Status);
        Assert.StartsWith("wamid.TEST", attempt.Wamid, StringComparison.Ordinal);
    }

    // ---------- Resultados y reintentos (regla 9) ----------

    [Fact]
    public async Task Transitorio_se_reintenta_con_backoff_y_falla_al_quinto_intento()
    {
        var job = await SeedJobAsync();
        var handler = Rejecting(131000);

        await RunAsync(handler);
        var first = await JobAsync(job);
        Assert.Equal(DispatchStatus.RetryScheduled, first.DispatchStatus);
        Assert.InRange(first.NextAttemptUtc!.Value, _clock.UtcNow.AddSeconds(15), _clock.UtcNow.AddSeconds(30));
        Assert.Null(first.LockedBy);

        // Antes de ProximoIntentoUtc no se reclama.
        Assert.Equal(0, (await RunAsync(handler)).Claimed);

        for (var i = 0; i < 4; i++)
        {
            var current = await JobAsync(job);
            _clock.Advance(current.NextAttemptUtc!.Value - _clock.UtcNow + TimeSpan.FromSeconds(1));
            await RunAsync(handler);
        }

        var last = await JobAsync(job);
        Assert.Equal(DispatchStatus.Failed, last.DispatchStatus);
        Assert.Equal(5, last.Attempts);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(await AttemptsAsync(job), a => Assert.Equal(SendAttemptStatus.Rejected, a.Status));
    }

    [Fact]
    public async Task No_transitorio_falla_sin_reintento()
    {
        var job = await SeedJobAsync();
        var handler = Rejecting(131026);

        await RunAsync(handler);
        _clock.Advance(TimeSpan.FromHours(1));
        await RunAsync(handler);

        Assert.Equal(DispatchStatus.Failed, (await JobAsync(job)).DispatchStatus);
        Assert.Single(handler.Requests);
        Assert.Equal(131026, Assert.Single(await AttemptsAsync(job)).ErrorCode);
    }

    [Fact]
    public async Task Reintento_que_pasaria_ExpiraUtc_queda_Expired()
    {
        var job = await SeedJobAsync(expiresIn: TimeSpan.FromSeconds(10));
        var handler = Rejecting(131000);

        await RunAsync(handler);

        Assert.Equal(DispatchStatus.Expired, (await JobAsync(job)).DispatchStatus);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Timeout_queda_NeedsReview_sin_reenvio()
    {
        var job = await SeedJobAsync();
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return MetaWhatsAppClientTests.Json(HttpStatusCode.OK, MetaWhatsAppClientTests.AcceptedJson);
        });

        await using (var db = database.CreateContext())
        {
            await NewDispatcher(db, handler, "B", TimeSpan.FromMilliseconds(200)).RunOnceAsync(CancellationToken.None);
        }

        _clock.Advance(TimeSpan.FromHours(3));
        await RunAsync(handler);

        Assert.Equal(DispatchStatus.NeedsReview, (await JobAsync(job)).DispatchStatus);
        Assert.Single(handler.Requests);
        Assert.Equal(SendAttemptStatus.SubmissionUnknown, Assert.Single(await AttemptsAsync(job)).Status);
    }

    [Fact]
    public async Task Webhook_de_un_SubmissionUnknown_pasa_el_trabajo_de_NeedsReview_a_Submitted()
    {
        var job = await SeedJobAsync(DispatchStatus.NeedsReview);
        var attemptId = Guid.CreateVersion7();
        await using (var db = database.CreateContext())
        {
            db.SendAttempts.Add(new SendAttempt
            {
                AttemptId = attemptId, DispatchJobId = job, StartedUtc = Start, Status = SendAttemptStatus.SubmissionUnknown,
            });
            db.WebhookInbox.Add(new WebhookInbox
            {
                ReceivedUtc = Start,
                Body = $$$"""
                    {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{
                    "messaging_product":"whatsapp","statuses":[{"id":"wamid.LATE","status":"sent","timestamp":"1763380800",
                    "recipient_id":"59170000000","biz_opaque_callback_data":"{{{attemptId}}}"}]}}]}]}
                    """,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            var processor = TestServices.Processor(db, _clock);
            await processor.ProcessBatchAsync(CancellationToken.None);
        }

        Assert.Equal(DispatchStatus.Submitted, (await JobAsync(job)).DispatchStatus);
        var attempt = Assert.Single(await AttemptsAsync(job));
        Assert.Equal(SendAttemptStatus.Accepted, attempt.Status);
        Assert.Equal("wamid.LATE", attempt.Wamid);
    }
}
