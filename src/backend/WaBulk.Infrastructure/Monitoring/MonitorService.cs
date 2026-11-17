using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Alerts;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Capacity;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Capacity;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Monitoring;

/// <summary>Sección "Monitor" de la Api (F1.8).</summary>
public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    /// <summary>Cada cuántos segundos la Api recalcula el tablero y lo empuja por SignalR si cambió (meta: &lt; 5 s).</summary>
    public int PushSeconds { get; set; } = 2;

    /// <summary>Horas sin webhook tras las que un envío incierto se marca como vencido (igual que Dispatcher:NeedsReviewHours).</summary>
    public int NeedsReviewHours { get; set; } = 2;
}

/// <summary>Contadores de una campaña (versión vigente). Entregados incluye los leídos.</summary>
public sealed record CampaignCounters(
    long CampaignId, string Name, CampaignStatus Status, int? Version, int Total, int Queued, int Sending, int OnHold, int Sent,
    int Delivered, int Read, int Failed, int NeedsReview, int Expired, IReadOnlyDictionary<string, int> Skipped);

/// <summary>Capacidad del portafolio en las últimas 24 h (destinatarios únicos fuera de la ventana de atención).</summary>
public sealed record CapacityToday(int Limit, string LimitSource, int Budget, int Used, int Remaining);

public sealed record PauseState(bool Paused, string? Reason, DateTime? SinceUtc, int OpenAlerts);

public sealed record MonitorSnapshot(PauseState Pause, CapacityToday Capacity, IReadOnlyList<CampaignCounters> Campaigns);

public sealed record OptOutItem(string PhoneE164, string? Name, string Layer, string State, string Source, DateTime SinceUtc, string? Evidence);

public sealed record ReviewItem(
    long JobId, long? CampaignId, string? CampaignName, string PhoneE164, string? ContactName, Guid AttemptId, DateTime StartedUtc,
    bool Overdue, string? Detail);

public sealed record MonitorResult(string? Error, string Message)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Tablero (F1.8): contadores por campaña, pausa global, capacidad del día, bajas vigentes y envíos inciertos. La Api lo
/// consulta para las pantallas y MonitorBroadcaster lo empuja por SignalR. Solo lectura, salvo marcar un envío incierto
/// como "llegó" o "no llegó" (auditado, nunca reenvía: regla 4).
/// </summary>
public sealed class MonitorService(
    WaBulkDbContext db, CapacityService capacity, IOptions<CapacityOptions> capacityOptions, IOptions<MonitorOptions> options,
    TimeProvider clock)
{
    private static readonly CampaignStatus[] Visible =
        [CampaignStatus.Scheduled, CampaignStatus.Running, CampaignStatus.Paused, CampaignStatus.DispatchCompleted];

    public async Task<MonitorSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        new(await PauseAsync(cancellationToken), await CapacityAsync(cancellationToken), await CountersAsync(cancellationToken));

    public async Task<IReadOnlyList<CampaignCounters>> CountersAsync(CancellationToken cancellationToken)
    {
        var campaigns = await (from c in db.Campaigns.AsNoTracking()
                               join v in db.CampaignVersions.AsNoTracking() on c.CurrentVersionId equals v.Id
                               where Visible.Contains(c.Status)
                               orderby c.Id descending
                               select new { c.Id, c.Name, c.Status, VersionId = v.Id, v.Number }).ToListAsync(cancellationToken);
        if (campaigns.Count == 0)
        {
            return [];
        }

        var versionIds = campaigns.Select(c => c.VersionId).ToList();
        var jobs = await db.DispatchJobs.AsNoTracking()
            .Where(j => j.CampaignVersionId != null && versionIds.Contains(j.CampaignVersionId.Value))
            .GroupBy(j => new { j.CampaignVersionId, j.DispatchStatus, j.SkipReason })
            .Select(g => new { g.Key.CampaignVersionId, g.Key.DispatchStatus, g.Key.SkipReason, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var delivery = await (from a in db.SendAttempts.AsNoTracking()
                              join j in db.DispatchJobs.AsNoTracking() on a.DispatchJobId equals j.Id
                              where j.CampaignVersionId != null && versionIds.Contains(j.CampaignVersionId.Value)
                                    && a.Status == SendAttemptStatus.Accepted && a.DeliveryStatus != null
                              group a by new { j.CampaignVersionId, a.DeliveryStatus } into g
                              select new { g.Key.CampaignVersionId, g.Key.DeliveryStatus, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return [.. campaigns.Select(c =>
        {
            int Jobs(params DispatchStatus[] s) => jobs.Where(j => j.CampaignVersionId == c.VersionId && s.Contains(j.DispatchStatus)).Sum(j => j.Count);
            int Delivery(params DeliveryStatus[] s) =>
                delivery.Where(d => d.CampaignVersionId == c.VersionId && s.Contains(d.DeliveryStatus!.Value)).Sum(d => d.Count);
            var skipped = jobs.Where(j => j.CampaignVersionId == c.VersionId && j.DispatchStatus == DispatchStatus.Skipped)
                .GroupBy(j => j.SkipReason ?? "SinMotivo").ToDictionary(g => g.Key, g => g.Sum(j => j.Count));
            return new CampaignCounters(
                c.Id, c.Name, c.Status, c.Number,
                Total: jobs.Where(j => j.CampaignVersionId == c.VersionId).Sum(j => j.Count),
                Queued: Jobs(DispatchStatus.Queued, DispatchStatus.RetryScheduled),
                Sending: Jobs(DispatchStatus.Claimed),
                OnHold: Jobs(DispatchStatus.OnHold),
                Sent: Jobs(DispatchStatus.Submitted),
                Delivered: Delivery(DeliveryStatus.Delivered, DeliveryStatus.Read),
                Read: Delivery(DeliveryStatus.Read),
                Failed: Jobs(DispatchStatus.Failed) + Delivery(DeliveryStatus.Failed),
                NeedsReview: Jobs(DispatchStatus.NeedsReview),
                Expired: Jobs(DispatchStatus.Expired),
                Skipped: skipped);
        })];
    }

    public async Task<PauseState> PauseAsync(CancellationToken cancellationToken)
    {
        var pause = await db.DispatchPauses.AsNoTracking().Where(p => p.LiftedUtc == null).OrderBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var open = await db.Alerts.AsNoTracking().CountAsync(a => a.ResolvedUtc == null, cancellationToken);
        return new PauseState(pause is not null, pause?.Reason.ToString(), pause?.FromUtc, open);
    }

    public async Task<CapacityToday> CapacityAsync(CancellationToken cancellationToken)
    {
        var o = capacityOptions.Value;
        var real = await db.MessagingLimits.AsNoTracking().Where(m => m.PortfolioId == o.PortfolioId)
            .Select(m => new { m.Limit, m.MetaValue }).FirstOrDefaultAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var used = await db.CapacityReservations.AsNoTracking()
            .Where(r => r.PortfolioId == o.PortfolioId && r.ExpiresUtc > now
                        && (r.Status == ReservationStatus.Reservada || r.Status == ReservationStatus.Consumida))
            .Select(r => r.PhoneE164).Distinct().CountAsync(cancellationToken);
        var budget = await capacity.BudgetAsync(cancellationToken);
        return new CapacityToday(real?.Limit ?? o.PortfolioLimit, real is null ? "Configuración" : $"Meta ({real.MetaValue})",
            budget, used, Math.Max(0, budget - used));
    }

    /// <summary>
    /// Bajas vigentes: capa empresa (último ConsentRecord de promociones en OptOut o No contactar) y capa plataforma
    /// (última preferencia de marketing = stop). Una persona puede aparecer en las dos.
    /// </summary>
    public async Task<IReadOnlyList<OptOutItem>> OptOutsAsync(CancellationToken cancellationToken)
    {
        var company = await db.ConsentRecords.AsNoTracking()
            .Where(c => c.Purpose == ConsentPurposes.Promociones)
            .GroupBy(c => c.PhoneE164)
            .Select(g => g.OrderByDescending(c => c.RecordedUtc).ThenByDescending(c => c.Id).First())
            .ToListAsync(cancellationToken);
        var platform = await db.PlatformPreferences.AsNoTracking()
            .Where(p => p.Category == PlatformCategories.MarketingMessages)
            .GroupBy(p => p.PhoneE164)
            .Select(g => g.OrderByDescending(p => p.RecordedUtc).ThenByDescending(p => p.Id).First())
            .ToListAsync(cancellationToken);
        var phones = company.Select(c => c.PhoneE164).Concat(platform.Select(p => p.PhoneE164)).Distinct().ToList();
        var names = await db.Contacts.AsNoTracking().Where(c => c.PhoneE164 != null && phones.Contains(c.PhoneE164))
            .ToDictionaryAsync(c => c.PhoneE164!, c => c.Name, cancellationToken);

        var items = company.Where(c => c.Status is ConsentStatus.OptOut or ConsentStatus.NoContactar)
            .Select(c => new OptOutItem(c.PhoneE164, names.GetValueOrDefault(c.PhoneE164), "Empresa", c.Status.ToString(),
                c.Source.ToString(), c.RecordedUtc, c.Evidence))
            .Concat(platform.Where(p => p.Value == PlatformPreferenceValue.Stop)
                .Select(p => new OptOutItem(p.PhoneE164, names.GetValueOrDefault(p.PhoneE164), "Plataforma", "Stop",
                    p.Origin.ToString(), p.RecordedUtc, null)));
        return [.. items.OrderByDescending(i => i.SinceUtc)];
    }

    /// <summary>Envíos inciertos (NeedsReview). Overdue = sin webhook pasado el plazo: ya no se espera que se resuelvan solos.</summary>
    public async Task<IReadOnlyList<ReviewItem>> ReviewAsync(CancellationToken cancellationToken)
    {
        var overdueBefore = clock.GetUtcNow().UtcDateTime.AddHours(-options.Value.NeedsReviewHours);
        var rows = await (from j in db.DispatchJobs.AsNoTracking()
                          join a in db.SendAttempts.AsNoTracking() on j.Id equals a.DispatchJobId
                          join ct in db.Contacts.AsNoTracking() on j.ContactId equals ct.Id
                          from v in db.CampaignVersions.AsNoTracking().Where(v => v.Id == j.CampaignVersionId).DefaultIfEmpty()
                          from c in db.Campaigns.AsNoTracking().Where(c => v != null && c.Id == v.CampaignId).DefaultIfEmpty()
                          where j.DispatchStatus == DispatchStatus.NeedsReview && a.Status == SendAttemptStatus.SubmissionUnknown
                          orderby a.StartedUtc
                          select new { j.Id, CampaignId = (long?)c.Id, CampaignName = c.Name, j.PhoneE164, ct.Name, a.AttemptId, a.StartedUtc, a.Detail })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new ReviewItem(r.Id, r.CampaignId, r.CampaignName, r.PhoneE164, r.Name, r.AttemptId, r.StartedUtc,
            r.StartedUtc <= overdueBefore, r.Detail))];
    }

    /// <summary>
    /// Una persona confirma si un envío incierto llegó (por ejemplo, preguntando al cliente): llegó → Submitted; no llegó →
    /// Failed. Nunca reenvía (regla 4): si hace falta, el contacto entra en una versión nueva de la campaña. El intento
    /// sigue SubmissionUnknown (para Meta no hubo respuesta); la decisión queda en AuditLog.
    /// </summary>
    public async Task<MonitorResult> MarkReviewAsync(string actor, long jobId, bool arrived, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var to = arrived ? DispatchStatus.Submitted : DispatchStatus.Failed;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var changed = await db.DispatchJobs.Where(j => j.Id == jobId && j.DispatchStatus == DispatchStatus.NeedsReview)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.DispatchStatus, to), cancellationToken);
        if (changed == 0)
        {
            return new MonitorResult("NoPendiente", "El envío ya no está pendiente de revisión (lo resolvió un webhook u otra persona).");
        }

        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.RevisarEnvio, "DispatchJob", jobId.ToString(CultureInfo.InvariantCulture),
            new { estado = DispatchStatus.NeedsReview.ToString() }, new { estado = to.ToString(), llego = arrived }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new MonitorResult(null, arrived
            ? "Marcado como enviado (llegó)."
            : "Marcado como no enviado. No se reenvía solo: si corresponde, inclúyelo en una versión nueva de la campaña.");
    }
}
