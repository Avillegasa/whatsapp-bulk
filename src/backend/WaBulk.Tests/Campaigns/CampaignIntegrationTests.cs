using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Messaging;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Campaigns;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Tests.Meta;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Campaigns;

/// <summary>
/// F1.6 contra SQL Server: versiones inmutables, aprobación de cuatro ojos, trabajos creados una sola vez al aprobar,
/// pausa / reanudación / cancelación, vencimiento y el despachador tomando plantilla y parámetros de la versión.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CampaignIntegrationTests(SqlServerTestDatabase database)
    : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private const string Operador = "operador1";
    private const string Aprobador = "aprobador1";
    private static readonly DateTime Start = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
    private readonly ManualClock _clock = new(Start);
    private long _templateId;
    private long _segmentId;
    private int _wamid;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        await using var db = database.CreateContext();
        var template = new Template
        {
            MetaTemplateId = "1696871368624750", Name = "promo_f15", Language = "es", Category = TemplateCategories.Marketing,
            Status = TemplateStatuses.Approved, ParameterFormat = "NAMED", StatusUtc = Start, CreatedUtc = Start, CreatedBy = "meta",
            Components = """[{"type":"BODY","text":"Hola {{nombre}}, esta semana {{promocion}}. ¿Te interesa?"},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Me interesa"},{"type":"QUICK_REPLY","text":"No recibir más"}]}]""",
        };
        db.Templates.Add(template);
        var segment = new Segment { Name = "Demo", CreatedUtc = Start, CreatedBy = Operador };
        db.Segments.Add(segment);
        await db.SaveChangesAsync();
        _templateId = template.Id;
        _segmentId = segment.Id;

        // Ana y Beto aceptan promociones; Caro no dio consentimiento; Dani no tiene teléfono (solo BSUID).
        foreach (var (phone, name, optIn) in new[] { ("+59170000001", "Ana Pérez", true), ("+59170000002", (string?)null, true), ("+59170000003", "Caro", false) })
        {
            var c = new Contact { PhoneE164 = phone, WaId = phone[1..], Name = name, CreatedUtc = Start, CreatedBy = "prueba" };
            db.Contacts.Add(c);
            await db.SaveChangesAsync();
            db.SegmentMembers.Add(new SegmentMember { SegmentId = segment.Id, ContactId = c.Id, AddedUtc = Start, AddedBy = Operador });
            if (optIn)
            {
                db.ConsentRecords.Add(new ConsentRecord
                {
                    ContactId = c.Id, PhoneE164 = phone, Status = ConsentStatus.OptIn, Source = ConsentSource.Boton,
                    RecordedUtc = Start.AddDays(-1), RecordedBy = "webhook",
                });
            }
        }

        var noPhone = new Contact { UserId = "BO.1", Name = "Dani", CreatedUtc = Start, CreatedBy = "prueba" };
        db.Contacts.Add(noPhone);
        await db.SaveChangesAsync();
        db.SegmentMembers.Add(new SegmentMember { SegmentId = segment.Id, ContactId = noPhone.Id, AddedUtc = Start, AddedBy = Operador });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Versión y aprobación ----------

    [Fact]
    public async Task Crear_congela_la_audiencia_y_calcula_el_hash()
    {
        var id = await CreateAsync();

        await using var db = database.CreateContext();
        var campaign = await db.Campaigns.SingleAsync(c => c.Id == id);
        var version = await db.CampaignVersions.SingleAsync();
        Assert.Equal((CampaignStatus.Draft, version.Id), (campaign.Status, campaign.CurrentVersionId));
        Assert.Equal((3, 1), (version.AudienceCount, version.ExcludedCount)); // Dani (sin teléfono) queda fuera.
        Assert.Matches("^[0-9a-f]{64}$", version.Hash);
        Assert.Equal(CampaignRules.ComputeHash(version), version.Hash);
        Assert.Equal(AuditActions.CrearCampana, (await db.AuditLogs.SingleAsync()).Action);
    }

    [Fact]
    public async Task El_creador_no_puede_aprobar()
    {
        var id = await CreateAndSubmitAsync();
        var version = await CurrentVersionAsync(id);

        var result = await Service(s => s.ApproveAsync(Operador, id, version.Id, version.Hash, CancellationToken.None));

        Assert.Equal(CampaignErrors.Prohibido, result.Error);
        await using var db = database.CreateContext();
        Assert.Equal(CampaignStatus.PendingApproval, (await db.Campaigns.SingleAsync()).Status);
        Assert.Empty(await db.DispatchJobs.ToListAsync());
    }

    [Fact]
    public async Task Aprobar_crea_un_trabajo_por_destinatario_con_sus_parametros_y_bajas_revisadas()
    {
        var id = await CreateAndSubmitAsync();
        var version = await CurrentVersionAsync(id);

        var result = await Service(s => s.ApproveAsync(Aprobador, id, version.Id, version.Hash, CancellationToken.None));

        Assert.True(result.Succeeded, result.Message);
        await using var db = database.CreateContext();
        Assert.Equal(CampaignStatus.Scheduled, (await db.Campaigns.SingleAsync()).Status);
        var approved = await db.CampaignVersions.SingleAsync();
        Assert.Equal((Aprobador, Start), (approved.ApprovedBy, approved.ApprovedUtc));
        var jobs = await db.DispatchJobs.OrderBy(j => j.PhoneE164).ToListAsync();
        Assert.Equal([DispatchStatus.Queued, DispatchStatus.Queued, DispatchStatus.Skipped], jobs.Select(j => j.DispatchStatus));
        Assert.Equal(SkipReasons.SinConsentimiento, jobs[2].SkipReason);
        Assert.All(jobs, j => Assert.Equal((version.Id, version.ScheduledUtc, version.ExpiresUtc), (j.CampaignVersionId!.Value, j.ScheduledUtc, j.ExpiresUtc)));
        // {{nombre}}: primer nombre del contacto, o el texto por defecto si no tiene.
        Assert.Equal([("nombre", "Ana"), ("promocion", "2x1 en cortes")], Params(jobs[0]));
        Assert.Equal([("nombre", "cliente"), ("promocion", "2x1 en cortes")], Params(jobs[1]));
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.AprobarCampana);
        Assert.Equal(Aprobador, audit.User);
        Assert.Contains("\"trabajos\":3", audit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dos_aprobaciones_simultaneas_crean_los_trabajos_una_sola_vez()
    {
        var id = await CreateAndSubmitAsync();
        var version = await CurrentVersionAsync(id);

        var results = await Task.WhenAll(
            Service(s => s.ApproveAsync(Aprobador, id, version.Id, version.Hash, CancellationToken.None)),
            Service(s => s.ApproveAsync("aprobador2", id, version.Id, version.Hash, CancellationToken.None)));

        // La segunda ve la campaña ya aprobada (EstadoNoPermite) o choca con el UPDATE condicional (Conflicto), según el momento.
        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.Error is CampaignErrors.Conflicto or CampaignErrors.Estado);
        await using var db = database.CreateContext();
        Assert.Equal(3, await db.DispatchJobs.CountAsync());
        Assert.Single(await db.AuditLogs.Where(a => a.Action == AuditActions.AprobarCampana).ToListAsync());
    }

    [Fact]
    public async Task Si_los_trabajos_ya_existen_el_indice_unico_revierte_toda_la_aprobacion()
    {
        var id = await CreateAndSubmitAsync();
        var version = await CurrentVersionAsync(id);
        await using (var db = database.CreateContext())
        {
            // Segunda barrera: un trabajo de esta versión ya existe (como si otra aprobación lo hubiera creado).
            var ana = await db.Contacts.SingleAsync(c => c.PhoneE164 == "+59170000001");
            db.DispatchJobs.Add(new DispatchJob
            {
                CampaignVersionId = version.Id, ContactId = ana.Id, PhoneE164 = ana.PhoneE164!, ScheduledUtc = Start, ExpiresUtc = Start.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        var result = await Service(s => s.ApproveAsync(Aprobador, id, version.Id, version.Hash, CancellationToken.None));

        Assert.Equal(CampaignErrors.Conflicto, result.Error);
        await using var check = database.CreateContext();
        Assert.Equal(CampaignStatus.PendingApproval, (await check.Campaigns.SingleAsync()).Status); // Nada de la transacción quedó.
        Assert.Null((await check.CampaignVersions.SingleAsync()).ApprovedBy);
        Assert.Equal(1, await check.DispatchJobs.CountAsync());
        Assert.Empty(await check.AuditLogs.Where(a => a.Action == AuditActions.AprobarCampana).ToListAsync());
    }

    [Fact]
    public async Task Editar_una_version_aprobada_crea_otra_que_exige_aprobacion_y_no_repite_envios()
    {
        var id = await CreateAndSubmitAsync();
        var v1 = await CurrentVersionAsync(id);
        await Service(s => s.ApproveAsync(Aprobador, id, v1.Id, v1.Hash, CancellationToken.None));
        await using (var db = database.CreateContext())
        {
            // Ana ya recibió la v1; Beto sigue en cola.
            await db.DispatchJobs.Where(j => j.PhoneE164 == "+59170000001")
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.DispatchStatus, DispatchStatus.Submitted));
        }

        var edit = await Service(s => s.NewVersionAsync(Operador, id, Draft(promo: "3x2 en pollo"), CancellationToken.None));

        Assert.True(edit.Succeeded, edit.Message);
        var v2 = await CurrentVersionAsync(id);
        Assert.Equal(2, v2.Number);
        Assert.NotEqual(v1.Hash, v2.Hash);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(CampaignStatus.Draft, (await db.Campaigns.SingleAsync()).Status);
            var old = await db.DispatchJobs.Where(j => j.CampaignVersionId == v1.Id).OrderBy(j => j.PhoneE164).ToListAsync();
            Assert.Equal([DispatchStatus.Submitted, DispatchStatus.Skipped, DispatchStatus.Skipped], old.Select(j => j.DispatchStatus));
            Assert.Equal(SkipReasons.VersionReemplazada, old[1].SkipReason);
            Assert.Null((await db.CampaignVersions.SingleAsync(v => v.Id == v1.Id)).RejectedBy); // La v1 no cambió.
        }

        // La v1 aprobada no sirve para la v2: hay que enviarla y aprobarla de nuevo.
        Assert.Equal(CampaignErrors.Conflicto, (await Service(s => s.ApproveAsync(Aprobador, id, v1.Id, v1.Hash, CancellationToken.None))).Error);
        Assert.True((await Service(s => s.SubmitAsync(Operador, id, CancellationToken.None))).Succeeded);
        Assert.True((await Service(s => s.ApproveAsync(Aprobador, id, v2.Id, v2.Hash, CancellationToken.None))).Succeeded);

        await using var check = database.CreateContext();
        var jobs = await check.DispatchJobs.Where(j => j.CampaignVersionId == v2.Id).OrderBy(j => j.PhoneE164).ToListAsync();
        Assert.Equal((DispatchStatus.Skipped, SkipReasons.YaEnviado), (jobs[0].DispatchStatus, jobs[0].SkipReason)); // Ana: no dos veces.
        Assert.Equal(DispatchStatus.Queued, jobs[1].DispatchStatus);
        Assert.Equal([("nombre", "cliente"), ("promocion", "3x2 en pollo")], Params(jobs[1]));
    }

    [Fact]
    public async Task Rechazar_vuelve_a_borrador_con_motivo()
    {
        var id = await CreateAndSubmitAsync();
        var version = await CurrentVersionAsync(id);

        Assert.Equal(CampaignErrors.Invalida, (await Service(s => s.RejectAsync(Aprobador, id, version.Id, " ", CancellationToken.None))).Error);
        var result = await Service(s => s.RejectAsync(Aprobador, id, version.Id, "Falta el precio", CancellationToken.None));

        Assert.True(result.Succeeded);
        await using var db = database.CreateContext();
        var campaign = await db.Campaigns.SingleAsync();
        Assert.Equal((CampaignStatus.Draft, "Falta el precio"), (campaign.Status, campaign.StatusReason));
        Assert.Equal(Aprobador, (await db.CampaignVersions.SingleAsync()).RejectedBy);
        Assert.Equal(CampaignErrors.Estado, (await Service(s => s.ApproveAsync(Aprobador, id, version.Id, version.Hash, CancellationToken.None))).Error);
    }

    [Fact]
    public async Task No_se_envia_a_aprobacion_con_la_plantilla_sin_aprobar_ni_con_parametros_incompletos()
    {
        var invalid = await Service(s => s.CreateAsync(Operador, Draft() with { Parameters = [new("nombre", ParameterSource.Fijo, "x")] }, CancellationToken.None));
        Assert.Equal(CampaignErrors.Invalida, invalid.Error);
        Assert.Contains("{{promocion}}", invalid.Message, StringComparison.Ordinal);

        var id = await CreateAsync();
        await using (var db = database.CreateContext())
        {
            await db.Templates.ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TemplateStatuses.Pending));
        }

        var submit = await Service(s => s.SubmitAsync(Operador, id, CancellationToken.None));
        Assert.Equal(CampaignErrors.Invalida, submit.Error);
        Assert.Contains("PENDING", submit.Message, StringComparison.Ordinal);
    }

    // ---------- Despacho ----------

    [Fact]
    public async Task El_despachador_envia_la_plantilla_y_los_parametros_de_la_version()
    {
        var id = await ApprovedAsync();
        var handler = Accepting();

        var round = await DispatchAsync(handler);

        Assert.Equal(2, round.Sent);
        var body = handler.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement).First(b => b.GetProperty("to").GetString() == "+59170000001");
        var template = body.GetProperty("template");
        Assert.Equal(("promo_f15", "es"), (template.GetProperty("name").GetString(), template.GetProperty("language").GetProperty("code").GetString()));
        var parameters = template.GetProperty("components")[0].GetProperty("parameters").EnumerateArray()
            .Select(p => (p.GetProperty("parameter_name").GetString(), p.GetProperty("text").GetString())).ToList();
        Assert.Equal([("nombre", "Ana"), ("promocion", "2x1 en cortes")], parameters);

        await DispatchAsync(handler); // Siguiente vuelta: sin pendientes → DispatchCompleted.
        await using var db = database.CreateContext();
        Assert.Equal(CampaignStatus.DispatchCompleted, (await db.Campaigns.SingleAsync(c => c.Id == id)).Status);
        _clock.Advance(TimeSpan.FromHours(73));
        await DispatchAsync(handler);
        await using var later = database.CreateContext();
        Assert.Equal(CampaignStatus.TrackingClosed, (await later.Campaigns.SingleAsync(c => c.Id == id)).Status);
    }

    [Fact]
    public async Task Cancelada_lo_pendiente_no_sale()
    {
        var id = await ApprovedAsync();

        var cancel = await Service(s => s.CancelAsync(Operador, id, "Se acabó el stock", CancellationToken.None));
        var handler = Accepting();
        await DispatchAsync(handler);

        Assert.True(cancel.Succeeded);
        Assert.Empty(handler.Requests);
        await using var db = database.CreateContext();
        Assert.Equal(CampaignStatus.Cancelled, (await db.Campaigns.SingleAsync()).Status);
        Assert.All(await db.DispatchJobs.ToListAsync(), j => Assert.Equal(DispatchStatus.Skipped, j.DispatchStatus));
        Assert.Equal(2, await db.DispatchJobs.CountAsync(j => j.SkipReason == SkipReasons.CampanaCancelada));
    }

    [Fact]
    public async Task Cancelada_despues_de_reclamar_el_despachador_la_revisa_antes_del_POST()
    {
        await ApprovedAsync();
        await using (var db = database.CreateContext())
        {
            // La cancelación llega sin pasar por el servicio (por ejemplo, entre el reclamo y el envío).
            await db.Campaigns.ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.Cancelled));
        }

        var handler = Accepting();
        var round = await DispatchAsync(handler);

        Assert.Empty(handler.Requests);
        Assert.Equal(2, round.Skipped);
        await using var check = database.CreateContext();
        Assert.Equal(2, await check.DispatchJobs.CountAsync(j => j.SkipReason == SkipReasons.CampanaCancelada));
    }

    [Fact]
    public async Task Pausa_retiene_y_reanudar_vuelve_a_la_cola()
    {
        var id = await ApprovedAsync();

        Assert.True((await Service(s => s.PauseAsync(Operador, id, "Revisar precio", CancellationToken.None))).Succeeded);
        var handler = Accepting();
        await DispatchAsync(handler);
        Assert.Empty(handler.Requests);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(2, await db.DispatchJobs.CountAsync(j => j.DispatchStatus == DispatchStatus.OnHold));
        }

        Assert.True((await Service(s => s.ResumeAsync(Operador, id, CancellationToken.None))).Succeeded);
        var round = await DispatchAsync(handler);

        Assert.Equal(2, round.Sent);
        await using var check = database.CreateContext();
        Assert.Equal(
            [AuditActions.AprobarCampana, AuditActions.CrearCampana, AuditActions.EnviarAprobacionCampana, AuditActions.PausarCampana, AuditActions.ReanudarCampana],
            (await check.AuditLogs.Select(a => a.Action).ToListAsync()).Order());
    }

    [Fact]
    public async Task Vencida_queda_Expired_sin_POST()
    {
        var id = await ApprovedAsync(expiresIn: TimeSpan.FromHours(2));
        await Service(s => s.PauseAsync(Operador, id, null, CancellationToken.None));
        _clock.Advance(TimeSpan.FromHours(3));

        var handler = Accepting();
        await DispatchAsync(handler);

        Assert.Empty(handler.Requests);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.DispatchJobs.CountAsync(j => j.DispatchStatus == DispatchStatus.Expired));
        Assert.Equal(CampaignStatus.Expired, (await db.Campaigns.SingleAsync()).Status);
        Assert.Equal(CampaignErrors.Estado, (await Service(s => s.ResumeAsync(Operador, id, CancellationToken.None))).Error);
    }

    [Fact]
    public async Task Trabajo_sin_campania_se_omite_sin_POST()
    {
        await using (var db = database.CreateContext())
        {
            var contact = await db.Contacts.FirstAsync(c => c.PhoneE164 == "+59170000001");
            db.DispatchJobs.Add(new DispatchJob
            {
                CampaignVersionId = null, ContactId = contact.Id, PhoneE164 = contact.PhoneE164!, ScheduledUtc = Start, ExpiresUtc = Start.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        var handler = Accepting();
        await DispatchAsync(handler);

        Assert.Empty(handler.Requests);
        await using var check = database.CreateContext();
        Assert.Equal(SkipReasons.SinCampania, (await check.DispatchJobs.SingleAsync()).SkipReason);
    }

    // ---------- Controles de F1.7 ----------

    [Fact]
    public async Task Tope_semanal_omite_a_quien_ya_recibio_el_maximo()
    {
        await using (var db = database.CreateContext())
        {
            var ana = await db.Contacts.SingleAsync(c => c.PhoneE164 == "+59170000001");
            var beto = await db.Contacts.SingleAsync(c => c.PhoneE164 == "+59170000002");
            // Ana: 2 promociones en los últimos 7 días (una aceptada, una incierta: puede haber llegado).
            await SentAsync(db, ana, Start.AddDays(-1), SendAttemptStatus.Accepted, campaign: true);
            await SentAsync(db, ana, Start.AddDays(-3), SendAttemptStatus.SubmissionUnknown, campaign: true);
            // Beto: una de hace 8 días (fuera de la ventana), una rechazada y un send-test (no es promoción de campaña).
            await SentAsync(db, beto, Start.AddDays(-8), SendAttemptStatus.Accepted, campaign: true);
            await SentAsync(db, beto, Start.AddDays(-1), SendAttemptStatus.Rejected, campaign: true);
            await SentAsync(db, beto, Start.AddHours(-1), SendAttemptStatus.Accepted, campaign: false);
        }

        await ApprovedAsync();
        var handler = Accepting();
        var round = await DispatchAsync(handler, maxPerWeek: 2);

        Assert.Equal((1, 1), (round.Sent, round.Skipped));
        Assert.Equal("+59170000002", JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement.GetProperty("to").GetString());
        await using var check = database.CreateContext();
        var versionId = await check.CampaignVersions.Where(v => v.TemplateName == "promo_f15").Select(v => v.Id).SingleAsync();
        var anaJob = await check.DispatchJobs.SingleAsync(j => j.PhoneE164 == "+59170000001" && j.CampaignVersionId == versionId);
        Assert.Equal((DispatchStatus.Skipped, SkipReasons.LimiteFrecuencia), (anaJob.DispatchStatus, anaJob.SkipReason));
    }

    [Fact]
    public async Task Fuera_de_horario_no_reclama_ni_envia_y_sale_al_abrir()
    {
        await ApprovedAsync(expiresIn: TimeSpan.FromDays(2));
        var laPaz = new DispatcherOptions { AllowedFrom = "08:00", AllowedUntil = "20:00", TimeZone = "America/La_Paz" };
        var handler = Accepting();

        _clock.Advance(TimeSpan.FromHours(11)); // 01:00 UTC = 21:00 en La Paz.
        var night = await DispatchAsync(handler, laPaz);

        Assert.True(night.OutsideHours);
        Assert.Equal(0, night.Claimed);
        Assert.Empty(handler.Requests);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(2, await db.DispatchJobs.CountAsync(j => j.DispatchStatus == DispatchStatus.Queued));
        }

        _clock.Advance(TimeSpan.FromHours(11)); // 12:00 UTC = 08:00 en La Paz.
        var morning = await DispatchAsync(handler, laPaz);

        Assert.False(morning.OutsideHours);
        Assert.Equal(2, morning.Sent);
    }

    /// <summary>Un envío anterior al contacto: trabajo (de campaña o de send-test) con un intento en el estado dado.</summary>
    private static async Task SentAsync(WaBulkDbContext db, Contact contact, DateTime when, SendAttemptStatus status, bool campaign)
    {
        var job = new DispatchJob
        {
            CampaignVersionId = campaign ? await TestServices.CampaignVersionAsync(db) : null,
            ContactId = contact.Id, PhoneE164 = contact.PhoneE164!, ScheduledUtc = when, ExpiresUtc = when.AddDays(1),
            DispatchStatus = status == SendAttemptStatus.Rejected ? DispatchStatus.Failed : DispatchStatus.Submitted,
        };
        db.DispatchJobs.Add(job);
        await db.SaveChangesAsync();
        db.SendAttempts.Add(new SendAttempt { AttemptId = Guid.CreateVersion7(), DispatchJobId = job.Id, StartedUtc = when, Status = status });
        await db.SaveChangesAsync();
    }

    // ---------- Apoyo ----------

    private CampaignDraft Draft(string promo = "2x1 en cortes", TimeSpan? expiresIn = null) => new(
        "Promo de la semana", _templateId, _segmentId,
        [new("nombre", ParameterSource.NombreContacto, "cliente"), new("promocion", ParameterSource.Fijo, promo)],
        Start, Start + (expiresIn ?? TimeSpan.FromDays(2)));

    private async Task<long> CreateAsync(TimeSpan? expiresIn = null)
    {
        var r = await Service(s => s.CreateAsync(Operador, Draft(expiresIn: expiresIn), CancellationToken.None));
        Assert.True(r.Succeeded, r.Message);
        return r.CampaignId!.Value;
    }

    private async Task<long> CreateAndSubmitAsync(TimeSpan? expiresIn = null)
    {
        var id = await CreateAsync(expiresIn);
        var r = await Service(s => s.SubmitAsync(Operador, id, CancellationToken.None));
        Assert.True(r.Succeeded, r.Message);
        return id;
    }

    private async Task<long> ApprovedAsync(TimeSpan? expiresIn = null)
    {
        var id = await CreateAndSubmitAsync(expiresIn);
        var v = await CurrentVersionAsync(id);
        var r = await Service(s => s.ApproveAsync(Aprobador, id, v.Id, v.Hash, CancellationToken.None));
        Assert.True(r.Succeeded, r.Message);
        return id;
    }

    private async Task<CampaignVersion> CurrentVersionAsync(long campaignId)
    {
        await using var db = database.CreateContext();
        var current = await db.Campaigns.Where(c => c.Id == campaignId).Select(c => c.CurrentVersionId).SingleAsync();
        return await db.CampaignVersions.AsNoTracking().SingleAsync(v => v.Id == current);
    }

    private async Task<CampaignResult> Service(Func<CampaignService, Task<CampaignResult>> action)
    {
        await using var db = database.CreateContext();
        return await action(new CampaignService(db, new ConsentService(db, _clock), _clock, NullLogger<CampaignService>.Instance));
    }

    private static List<(string, string)> Params(DispatchJob job) =>
        [.. CampaignRules.DeserializeJobParameters(job.Parameters).Select(p => (p.Name, p.Value))];

    private FakeHttpHandler Accepting() => new((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
        $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.C{{Interlocked.Increment(ref _wamid)}}","message_status":"accepted"}]}""")));

    private async Task<DispatchRound> DispatchAsync(FakeHttpHandler handler, DispatcherOptions? custom = null, int maxPerWeek = 1000)
    {
        await using var db = database.CreateContext();
        var opts = custom ?? new DispatcherOptions();
        opts.WorkerId = "W";
        opts.LeaseSeconds = 120;
        var options = Options.Create(opts);
        var dispatcher = new Dispatcher(db, new DispatchQueue(db), new ConsentService(db, _clock), TestCapacity.Unlimited(db),
            new AlertService(db, _clock, NullLogger<AlertService>.Instance), TestServices.Lifecycle(db), TestServices.RateLimiter,
            TestServices.Frequency(maxPerWeek), MetaWhatsAppClientTests.CreateClient(handler),
            options, new DispatcherIdentity(options), _clock, NullLogger<Dispatcher>.Instance);
        return await dispatcher.RunOnceAsync(CancellationToken.None);
    }
}
