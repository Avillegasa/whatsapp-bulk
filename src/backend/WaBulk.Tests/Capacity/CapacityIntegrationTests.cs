using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Capacity;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Capacity;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Tests.Meta;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Capacity;

/// <summary>
/// Concurrencia y capacidad (F0.11) contra SQL Server. Verificación del plan: con 2 despachadores y presupuesto de
/// 10, como máximo 10 reservas y ningún trabajo procesado dos veces.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CapacityIntegrationTests(SqlServerTestDatabase database)
    : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly ManualClock _clock = new(Start);
    private long _campaignVersion;

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string PhoneN(int n) => $"+5917200{n:0000}";

    /// <summary>Meta falso seguro para varios hilos: acepta (o rechaza ciertos números) y anota a quién se envió.</summary>
    private sealed class FakeMeta(TimeSpan delay, IReadOnlySet<string>? rejectTo = null, int? rejectCode = null)
    {
        private int _counter;

        public ConcurrentQueue<string> SentTo { get; } = new();

        public FakeHttpHandler Handler => new(async (request, ct) =>
        {
            var to = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.GetProperty("to").GetString()!;
            SentTo.Enqueue(to);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            if (rejectTo is not null && rejectTo.Contains(to))
            {
                return MetaWhatsAppClientTests.Json(HttpStatusCode.BadRequest,
                    $$$"""{"error":{"message":"x","type":"OAuthException","code":{{{rejectCode}}},"error_data":{"details":"x"},"fbtrace_id":"T"}}""");
            }

            return MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
                $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.C{{Interlocked.Increment(ref _counter)}}","message_status":"accepted"}]}""");
        });
    }

    private async Task<List<long>> SeedJobsAsync(IEnumerable<string> phones)
    {
        await using var db = database.CreateContext();
        var ids = new List<long>();
        foreach (var phone in phones)
        {
            if (!await db.ConsentRecords.AnyAsync(c => c.PhoneE164 == phone))
            {
                db.ConsentRecords.Add(new ConsentRecord
                {
                    PhoneE164 = phone, Status = ConsentStatus.OptIn, Source = ConsentSource.Manual,
                    RecordedUtc = Start.AddDays(-1), RecordedBy = "prueba",
                });
                await db.SaveChangesAsync();
            }

            var job = await new EnqueueService(db, new ConsentService(db, _clock)).EnqueueAsync(
                new EnqueueRequest(Interlocked.Increment(ref _campaignVersion), await TestServices.ContactIdAsync(db, phone), phone, Start, Start.AddDays(3)), CancellationToken.None);
            ids.Add(job.Id);
        }

        return ids;
    }

    private async Task<DispatchRound> RunAsync(FakeHttpHandler handler, string owner, int budget, int batch = 10, TimeSpan? httpTimeout = null)
    {
        await using var db = database.CreateContext();
        var options = Options.Create(new DispatcherOptions { WorkerId = owner, BatchSize = batch, LeaseSeconds = 120 });
        var dispatcher = new Dispatcher(db, new DispatchQueue(db), new ConsentService(db, _clock), TestCapacity.With(db, budget), new AlertService(db, _clock, NullLogger<AlertService>.Instance),
            MetaWhatsAppClientTests.CreateClient(handler, httpTimeout), options, new DispatcherIdentity(options), _clock,
            NullLogger<Dispatcher>.Instance);
        return await dispatcher.RunOnceAsync(CancellationToken.None);
    }

    private async Task<List<CapacityReservation>> ReservationsAsync()
    {
        await using var db = database.CreateContext();
        return await db.CapacityReservations.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    private static int ActiveRecipients(IEnumerable<CapacityReservation> reservations, DateTime now) =>
        reservations.Where(r => r.Status != ReservationStatus.Liberada && r.ExpiresUtc > now).Select(r => r.PhoneE164).Distinct().Count();

    // ---------- Verificación del plan ----------

    [Fact]
    public async Task Dos_despachadores_con_presupuesto_10_hacen_10_envios_y_ningun_trabajo_dos_veces()
    {
        var jobs = await SeedJobsAsync(Enumerable.Range(0, 20).Select(PhoneN));
        var meta = new FakeMeta(TimeSpan.FromMilliseconds(30));

        // A y B corren a la vez, varias vueltas cada uno, cada uno con su conexión.
        async Task<List<DispatchRound>> Loop(string owner)
        {
            var rounds = new List<DispatchRound>();
            for (var i = 0; i < 4; i++)
            {
                rounds.Add(await RunAsync(meta.Handler, owner, budget: 10));
            }

            return rounds;
        }

        var results = await Task.WhenAll(Task.Run(() => Loop("A")), Task.Run(() => Loop("B")));

        Assert.Equal(10, meta.SentTo.Count);
        Assert.Equal(10, meta.SentTo.Distinct().Count());
        var reservations = await ReservationsAsync();
        Assert.Equal(10, ActiveRecipients(reservations, _clock.UtcNow));
        Assert.All(reservations.Where(r => r.Status != ReservationStatus.Liberada),
            r => Assert.Equal(ReservationStatus.Consumida, r.Status));

        await using var db = database.CreateContext();
        var attemptsPerJob = await db.SendAttempts.GroupBy(a => a.DispatchJobId).Select(g => g.Count()).ToListAsync();
        Assert.All(attemptsPerJob, n => Assert.Equal(1, n)); // Ningún trabajo procesado dos veces.
        Assert.Equal(10, attemptsPerJob.Count);
        var states = await db.DispatchJobs.Where(j => jobs.Contains(j.Id))
            .GroupBy(j => j.DispatchStatus).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        Assert.Equal(10, states[DispatchStatus.Submitted]);
        Assert.Equal(10, states[DispatchStatus.RetryScheduled]); // Esperan capacidad; nadie quedó Claimed.
        Assert.False(states.ContainsKey(DispatchStatus.Claimed));
        Assert.Equal(10, results.SelectMany(r => r).Sum(r => r.Sent));
    }

    [Fact]
    public async Task Cuarenta_reservas_en_paralelo_con_presupuesto_10_dan_exactamente_10()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 40).Select(n => Task.Run(async () =>
        {
            await using var db = database.CreateContext();
            return await TestCapacity.With(db, 10).ReserveAsync(n + 1, PhoneN(n), _clock.UtcNow, CancellationToken.None);
        })));

        Assert.Equal(10, outcomes.Count(o => o == ReserveOutcome.Reserved));
        Assert.Equal(30, outcomes.Count(o => o == ReserveOutcome.NoCapacity));
        Assert.Equal(10, (await ReservationsAsync()).Count);
    }

    // ---------- Reglas de la reserva ----------

    [Fact]
    public async Task El_mismo_destinatario_no_ocupa_un_lugar_nuevo()
    {
        await SeedJobsAsync([PhoneN(1), PhoneN(1)]); // Dos campañas al mismo número.
        var meta = new FakeMeta(TimeSpan.Zero);

        await RunAsync(meta.Handler, "A", budget: 1);

        Assert.Equal(2, meta.SentTo.Count);
        var reservations = await ReservationsAsync();
        Assert.Equal(2, reservations.Count);
        Assert.Equal(1, ActiveRecipients(reservations, _clock.UtcNow));
    }

    [Fact]
    public async Task Sin_capacidad_el_trabajo_vuelve_a_la_cola_y_sale_cuando_se_libera_la_ventana()
    {
        var jobs = await SeedJobsAsync([PhoneN(1), PhoneN(2)]);
        var meta = new FakeMeta(TimeSpan.Zero);

        var first = await RunAsync(meta.Handler, "A", budget: 1);
        var second = await RunAsync(meta.Handler, "A", budget: 1); // El pospuesto no se vuelve a reclamar enseguida.

        Assert.Equal(1, first.Sent);
        Assert.Equal(1, first.NoCapacity);
        Assert.Equal(0, second.Claimed);
        Assert.Single(meta.SentTo);
        await using (var db = database.CreateContext())
        {
            var waiting = await db.DispatchJobs.SingleAsync(j => j.Id == jobs[1]);
            Assert.Equal(DispatchStatus.RetryScheduled, waiting.DispatchStatus);
            Assert.Equal(Start.AddMinutes(5), waiting.NextAttemptUtc);
            Assert.Null(waiting.LockedBy);
            Assert.Equal(0, waiting.Attempts); // Sin POST no se cuenta intento.
        }

        _clock.Advance(TimeSpan.FromMinutes(6)); // Se reclama otra vez, pero la ventana sigue llena.
        Assert.Equal(1, (await RunAsync(meta.Handler, "A", budget: 1)).NoCapacity);

        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1)); // Ventana móvil de 24 h.
        var third = await RunAsync(meta.Handler, "A", budget: 1);

        Assert.Equal(1, third.Sent);
        Assert.Equal([PhoneN(1), PhoneN(2)], meta.SentTo.ToArray());
    }

    [Fact]
    public async Task Un_rechazo_libera_el_lugar()
    {
        await SeedJobsAsync([PhoneN(1), PhoneN(2)]);
        var meta = new FakeMeta(TimeSpan.Zero, rejectTo: new HashSet<string> { PhoneN(1) }, rejectCode: 131026);

        var round = await RunAsync(meta.Handler, "A", budget: 1);

        Assert.Equal(2, round.Sent); // El rechazado liberó el lugar dentro de la misma vuelta.
        var reservations = await ReservationsAsync();
        Assert.Equal(ReservationStatus.Liberada, reservations[0].Status);
        Assert.Equal(ReservationStatus.Consumida, reservations[1].Status);
    }

    [Fact]
    public async Task Sin_respuesta_de_Meta_el_lugar_queda_consumido()
    {
        await SeedJobsAsync([PhoneN(1)]);
        var meta = new FakeMeta(TimeSpan.FromSeconds(5));

        await RunAsync(meta.Handler, "A", budget: 5, httpTimeout: TimeSpan.FromMilliseconds(200));

        var reservation = Assert.Single(await ReservationsAsync());
        Assert.Equal(ReservationStatus.Consumida, reservation.Status); // Puede haberse enviado: cuenta.
    }

    [Fact]
    public async Task Caida_despues_de_reservar_y_antes_del_POST_libera_el_lugar_al_recuperar()
    {
        var jobs = await SeedJobsAsync([PhoneN(1), PhoneN(2)]);

        // A reclama el primero, reserva y "se cae" antes de insertar el intento.
        await using (var db = database.CreateContext())
        {
            await new DispatchQueue(db).ClaimBatchAsync("A", 1, _clock.UtcNow, TimeSpan.FromSeconds(120), CancellationToken.None);
            Assert.Equal(ReserveOutcome.Reserved,
                await TestCapacity.With(db, 1).ReserveAsync(jobs[0], PhoneN(1), _clock.UtcNow, CancellationToken.None));
        }

        _clock.Advance(TimeSpan.FromSeconds(121));
        var meta = new FakeMeta(TimeSpan.Zero);
        var round = await RunAsync(meta.Handler, "B", budget: 1);

        Assert.Equal(1, round.Recovered);
        Assert.Single(meta.SentTo); // Con presupuesto 1, la reserva de A ya no ocupa lugar.
        var reservations = await ReservationsAsync();
        Assert.Equal(ReservationStatus.Liberada, reservations[0].Status);
        Assert.Equal(ReservationStatus.Consumida, reservations[1].Status);
    }

    [Fact]
    public async Task Un_trabajo_omitido_por_baja_no_ocupa_capacidad()
    {
        var jobs = await SeedJobsAsync([PhoneN(1), PhoneN(2)]);
        await using (var db = database.CreateContext())
        {
            await new ConsentService(db, _clock).RecordCompanyAsync(
                PhoneN(1), ConsentStatus.OptOut, ConsentSource.Intranet, "baja", "prueba", CancellationToken.None);
        }

        var meta = new FakeMeta(TimeSpan.Zero);
        var round = await RunAsync(meta.Handler, "A", budget: 1);

        Assert.Equal(1, round.Skipped);
        Assert.Equal(1, round.Sent);
        Assert.Equal([PhoneN(2)], meta.SentTo.ToArray());
        Assert.Single(await ReservationsAsync());
    }
}
