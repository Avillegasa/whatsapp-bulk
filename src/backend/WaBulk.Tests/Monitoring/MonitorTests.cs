using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WaBulk.Api;
using WaBulk.Api.Monitoring;
using WaBulk.Domain.Alerts;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Capacity;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Capacity;
using WaBulk.Infrastructure.Identity;
using WaBulk.Infrastructure.Monitoring;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Tests.Auth;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Monitoring;

/// <summary>
/// F1.8: tablero (contadores, capacidad, pausa), bajas vigentes, envíos inciertos, reanudar desde la Api (auditado) y
/// SignalR: un cambio en la base llega al cliente en menos de 5 s.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MonitorTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private const string Password = "Clave-Segura-2026";
    private static readonly DateTime Start = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
    private readonly ManualClock _clock = new(Start);
    private WebApplicationFactory<ApiAssemblyMarker> _factory = null!;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        _factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:WaBulk", database.ConnectionString);
            b.UseSetting("Monitor:PushSeconds", "1");
            b.UseSetting("Capacity:PortfolioId", "TEST");
        });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ---------- Servicio ----------

    [Fact]
    public async Task Contadores_por_campania_capacidad_y_pausa()
    {
        var (versionId, jobs) = await SeedCampaignAsync(
            DispatchStatus.Queued, DispatchStatus.Submitted, DispatchStatus.Submitted, DispatchStatus.Submitted, DispatchStatus.Failed,
            DispatchStatus.Skipped, DispatchStatus.OnHold, DispatchStatus.NeedsReview);
        await using (var db = database.CreateContext())
        {
            // Envíos aceptados: uno leído, uno entregado, uno falló en la entrega.
            await AttemptAsync(db, jobs[1], SendAttemptStatus.Accepted, DeliveryStatus.Read);
            await AttemptAsync(db, jobs[2], SendAttemptStatus.Accepted, DeliveryStatus.Delivered);
            await AttemptAsync(db, jobs[3], SendAttemptStatus.Accepted, DeliveryStatus.Failed);
            db.CapacityReservations.Add(new CapacityReservation
            {
                PortfolioId = "TEST", PhoneE164 = "+59170000001", DispatchJobId = jobs[1], ReservedUtc = Start.AddHours(-1),
                ExpiresUtc = Start.AddHours(23), Status = ReservationStatus.Consumida,
            });
            db.MessagingLimits.Add(new MessagingLimit { PortfolioId = "TEST", Limit = 250, MetaValue = "TIER_250", Source = "Api", UpdatedUtc = Start });
            await db.SaveChangesAsync();
        }

        await using var ctx = database.CreateContext();
        var snapshot = await Service(ctx).SnapshotAsync(CancellationToken.None);

        var c = Assert.Single(snapshot.Campaigns);
        Assert.Equal((8, 1, 1, 3, 2, 1, 2, 1), (c.Total, c.Queued, c.OnHold, c.Sent, c.Delivered, c.Read, c.Failed, c.NeedsReview));
        Assert.Equal(1, c.Skipped[SkipReasons.SinConsentimiento]);
        // Límite real 250 × 90 % − 200 = 25; un destinatario usado.
        Assert.Equal((250, "Meta (TIER_250)", 25, 1, 24), (snapshot.Capacity.Limit, snapshot.Capacity.LimitSource,
            snapshot.Capacity.Budget, snapshot.Capacity.Used, snapshot.Capacity.Remaining));
        Assert.False(snapshot.Pause.Paused);
    }

    [Fact]
    public async Task Revision_llego_y_no_llego_auditados_sin_reenviar()
    {
        var (_, jobs) = await SeedCampaignAsync(DispatchStatus.NeedsReview, DispatchStatus.NeedsReview);
        await using (var db = database.CreateContext())
        {
            await AttemptAsync(db, jobs[0], SendAttemptStatus.SubmissionUnknown, null, Start.AddHours(-3));
            await AttemptAsync(db, jobs[1], SendAttemptStatus.SubmissionUnknown, null, Start.AddMinutes(-30));
        }

        await using var ctx = database.CreateContext();
        var service = Service(ctx);
        var pending = await service.ReviewAsync(CancellationToken.None);
        Assert.Equal([true, false], pending.Select(p => p.Overdue)); // Solo el de hace 3 h está vencido (plazo 2 h).

        Assert.True((await service.MarkReviewAsync("ana", jobs[0], arrived: true, CancellationToken.None)).Succeeded);
        Assert.True((await service.MarkReviewAsync("ana", jobs[1], arrived: false, CancellationToken.None)).Succeeded);
        Assert.Equal("NoPendiente", (await service.MarkReviewAsync("beto", jobs[1], arrived: true, CancellationToken.None)).Error);

        await using var check = database.CreateContext();
        var states = await check.DispatchJobs.OrderBy(j => j.Id).Select(j => j.DispatchStatus).ToListAsync();
        Assert.Equal([DispatchStatus.Submitted, DispatchStatus.Failed], states);
        Assert.Equal(2, await check.SendAttempts.CountAsync()); // Ningún intento nuevo: no se reenvió.
        Assert.Equal(2, await check.AuditLogs.CountAsync(a => a.Action == AuditActions.RevisarEnvio && a.User == "ana"));
    }

    [Fact]
    public async Task Bajas_vigentes_por_capa()
    {
        await using (var db = database.CreateContext())
        {
            db.ConsentRecords.AddRange(
                Consent("+59170000001", ConsentStatus.OptIn, Start.AddDays(-5)),
                Consent("+59170000001", ConsentStatus.OptOut, Start.AddDays(-1)),  // Baja vigente.
                Consent("+59170000002", ConsentStatus.OptOut, Start.AddDays(-5)),
                Consent("+59170000002", ConsentStatus.OptIn, Start.AddDays(-1)));  // Volvió a aceptar: no aparece.
            db.PlatformPreferences.AddRange(
                new PlatformPreference { PhoneE164 = "+59170000003", Value = PlatformPreferenceValue.Stop, Origin = PlatformPreferenceOrigin.Webhook, RecordedUtc = Start },
                new PlatformPreference { PhoneE164 = "+59170000004", Value = PlatformPreferenceValue.Stop, Origin = PlatformPreferenceOrigin.Webhook, RecordedUtc = Start.AddDays(-2) },
                new PlatformPreference { PhoneE164 = "+59170000004", Value = PlatformPreferenceValue.Resume, Origin = PlatformPreferenceOrigin.Webhook, RecordedUtc = Start.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        await using var ctx = database.CreateContext();
        var list = await Service(ctx).OptOutsAsync(CancellationToken.None);

        Assert.Equal([("+59170000003", "Plataforma"), ("+59170000001", "Empresa")], list.Select(b => (b.PhoneE164, b.Layer)));
    }

    // ---------- Api y SignalR ----------

    [Fact]
    public async Task Reanudar_desde_la_Api_levanta_la_pausa_y_audita_con_el_usuario_de_la_intranet()
    {
        await using (var db = database.CreateContext())
        {
            await TestServices.Processor(db, _clock).ProcessBatchAsync(CancellationToken.None); // (sin filas: no hace nada)
            await new WaBulk.Infrastructure.Alerts.AlertService(db, _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<WaBulk.Infrastructure.Alerts.AlertService>.Instance)
                .RaiseAsync(AlertType.Token, "global", "Error 190", null, "Despachador", CancellationToken.None);
        }

        var operador = await SessionAsync("ope-1", Roles.Operador);
        var aprobador = await SessionAsync("apro-1", Roles.Aprobador);
        Assert.Equal(HttpStatusCode.Forbidden, (await operador.Client.GetAsync(Rel("/api/alertas"))).StatusCode);
        var before = (await aprobador.Client.GetFromJsonAsync<AlertasDto>(Rel("/api/alertas")))!;
        Assert.True(before.Pausa.EnPausa);
        Assert.Equal("Token", Assert.Single(before.Abiertas).Tipo);

        Assert.Equal(HttpStatusCode.OK, (await aprobador.PostAsync("/api/alertas/reanudar")).StatusCode);

        await using var check = database.CreateContext();
        var pause = await check.DispatchPauses.SingleAsync();
        Assert.Equal("apro-1", pause.LiftedBy);
        var audit = await check.AuditLogs.SingleAsync(a => a.Action == AuditActions.ReanudarDespacho);
        Assert.Equal("apro-1", audit.User);
    }

    [Fact]
    public async Task SignalR_el_contador_cambia_en_el_navegador_en_menos_de_5_segundos()
    {
        var (_, jobs) = await SeedCampaignAsync(DispatchStatus.Queued, DispatchStatus.Queued);
        var operador = await SessionAsync("ope-2", Roles.Operador);
        var received = new List<TableroDto>();
        var changed = new TaskCompletionSource<TableroDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, MonitorHub.Path), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                o.Headers["Cookie"] = operador.CookieHeader;
            })
            .Build();
        connection.On<TableroDto>(MonitorHub.Event, t =>
        {
            lock (received)
            {
                received.Add(t);
            }

            if (t.Campanias.Any(c => c.Enviados == 1))
            {
                changed.TrySetResult(t);
            }
        });
        await connection.StartAsync();

        // Simula al Worker: un trabajo pasa a Submitted (Meta aceptó).
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using (var db = database.CreateContext())
        {
            await db.DispatchJobs.Where(j => j.Id == jobs[0]).ExecuteUpdateAsync(s => s.SetProperty(j => j.DispatchStatus, DispatchStatus.Submitted));
        }

        var update = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"tardó {watch.Elapsed.TotalSeconds:0.0} s");
        var counters = Assert.Single(update.Campanias);
        Assert.Equal((1, 1), (counters.EnCola, counters.Enviados));
    }

    [Fact]
    public async Task Sin_sesion_el_hub_rechaza_la_conexion()
    {
        var client = _factory.CreateClient();
        var negotiate = await client.PostAsync(Rel($"{MonitorHub.Path}/negotiate?negotiateVersion=1"), null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    // ---------- Apoyo ----------

    private MonitorService Service(WaBulkDbContext db)
    {
        var capacity = Options.Create(new CapacityOptions { PortfolioId = "TEST", PortfolioLimit = 1000, UsagePercent = 90, ManualQuota = 200 });
        return new MonitorService(db, new CapacityService(db, capacity), capacity, Options.Create(new MonitorOptions()), _clock);
    }

    private async Task<(long VersionId, List<long> Jobs)> SeedCampaignAsync(params DispatchStatus[] states)
    {
        await using var db = database.CreateContext();
        var versionId = await TestServices.CampaignVersionAsync(db, status: CampaignStatus.Running);
        var jobs = new List<long>();
        for (var i = 0; i < states.Length; i++)
        {
            var phone = $"+5917000{i:0000}";
            var job = new DispatchJob
            {
                CampaignVersionId = versionId, ContactId = await TestServices.ContactIdAsync(db, phone), PhoneE164 = phone,
                ScheduledUtc = Start, ExpiresUtc = Start.AddDays(1), DispatchStatus = states[i],
                SkipReason = states[i] == DispatchStatus.Skipped ? SkipReasons.SinConsentimiento : null,
            };
            db.DispatchJobs.Add(job);
            await db.SaveChangesAsync();
            jobs.Add(job.Id);
        }

        return (versionId, jobs);
    }

    private static async Task AttemptAsync(WaBulkDbContext db, long jobId, SendAttemptStatus status, DeliveryStatus? delivery, DateTime? started = null)
    {
        db.SendAttempts.Add(new SendAttempt
        {
            AttemptId = Guid.CreateVersion7(), DispatchJobId = jobId, StartedUtc = started ?? Start, Status = status, DeliveryStatus = delivery,
        });
        await db.SaveChangesAsync();
    }

    private static ConsentRecord Consent(string phone, ConsentStatus status, DateTime when) => new()
    {
        PhoneE164 = phone, Status = status, Source = ConsentSource.Manual, RecordedUtc = when, RecordedBy = "prueba",
    };

    private static Uri Rel(string path) => new(path, UriKind.Relative);

    private async Task<ApiSession> SessionAsync(string userName, string role)
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = userName };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var session = new ApiSession(_factory.CreateClient());
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(userName, Password)).StatusCode);
        return session;
    }
}
