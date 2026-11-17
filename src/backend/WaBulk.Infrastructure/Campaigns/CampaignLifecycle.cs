using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Campaigns;

/// <summary>Sección "Campanias".</summary>
public sealed class CampaignOptions
{
    public const string SectionName = "Campanias";

    /// <summary>Horas de seguimiento (estados de entrega) después de terminar el despacho; luego TrackingClosed.</summary>
    public int TrackingHours { get; set; } = 72;
}

public sealed record LifecycleRound(int Running, int Completed, int Expired, int Closed);

/// <summary>
/// Cambios de estado automáticos de las campañas (F1.6), sin auditoría porque no los hace una persona. Los corre el
/// despachador al inicio de cada vuelta, con sentencias por conjunto (cada una idempotente):
/// - Scheduled con la programación cumplida → Running.
/// - Running sin trabajos pendientes → DispatchCompleted; o Expired si venció sin ningún envío.
/// - Draft, PendingApproval o Paused con la versión vencida → Expired (sus trabajos OnHold vencen en la cola).
/// - DispatchCompleted hace más de TrackingHours → TrackingClosed.
/// </summary>
public sealed class CampaignLifecycle(WaBulkDbContext db, IOptions<CampaignOptions> options)
{
    private static readonly DispatchStatus[] Open =
        [DispatchStatus.Queued, DispatchStatus.RetryScheduled, DispatchStatus.Claimed, DispatchStatus.OnHold];

    private static readonly DispatchStatus[] Sent = [DispatchStatus.Submitted, DispatchStatus.NeedsReview];

    public async Task<LifecycleRound> AdvanceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var running = await db.Campaigns
            .Where(c => c.Status == CampaignStatus.Scheduled
                        && db.CampaignVersions.Any(v => v.Id == c.CurrentVersionId && v.ScheduledUtc <= nowUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.Running).SetProperty(c => c.StatusUtc, nowUtc),
                cancellationToken);

        var finished = db.Campaigns.Where(c => c.Status == CampaignStatus.Running
            && !db.DispatchJobs.Any(j => j.CampaignVersionId == c.CurrentVersionId && Open.Contains(j.DispatchStatus)));

        var expiredUnsent = await finished
            .Where(c => db.CampaignVersions.Any(v => v.Id == c.CurrentVersionId && v.ExpiresUtc <= nowUtc)
                        && !db.DispatchJobs.Any(j => j.CampaignVersionId == c.CurrentVersionId && Sent.Contains(j.DispatchStatus)))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.Expired).SetProperty(c => c.StatusUtc, nowUtc),
                cancellationToken);

        var completed = await finished
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CampaignStatus.DispatchCompleted)
                .SetProperty(c => c.StatusUtc, nowUtc)
                .SetProperty(c => c.CompletedUtc, nowUtc), cancellationToken);

        var expired = await db.Campaigns
            .Where(c => (c.Status == CampaignStatus.Draft || c.Status == CampaignStatus.PendingApproval || c.Status == CampaignStatus.Paused)
                        && db.CampaignVersions.Any(v => v.Id == c.CurrentVersionId && v.ExpiresUtc <= nowUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.Expired).SetProperty(c => c.StatusUtc, nowUtc),
                cancellationToken);

        var closeBefore = nowUtc.AddHours(-options.Value.TrackingHours);
        var closed = await db.Campaigns
            .Where(c => c.Status == CampaignStatus.DispatchCompleted && c.CompletedUtc <= closeBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.TrackingClosed).SetProperty(c => c.StatusUtc, nowUtc),
                cancellationToken);

        return new LifecycleRound(running, completed, expired + expiredUnsent, closed);
    }
}
