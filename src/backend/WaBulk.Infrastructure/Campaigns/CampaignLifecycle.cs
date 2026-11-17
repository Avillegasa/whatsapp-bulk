using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Audit;
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

    /// <summary>Actor de los cambios que hace el despachador (aparece en AuditLog).</summary>
    public const string SystemActor = "sistema:despachador";

    /// <summary>
    /// Pausa una campaña que está enviando por un problema de su plantilla (F1.7): plantilla que dejó de estar APPROVED o
    /// error 132000–132016 al enviar. Lo pendiente queda OnHold y se audita; una persona revisa y reanuda (o crea una
    /// versión nueva con otra plantilla). Devuelve false si la campaña ya no estaba enviando.
    /// </summary>
    public async Task<bool> PauseForTemplateAsync(long campaignId, string reason, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var text = reason.Length <= 1000 ? reason : reason[..1000];
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var versionId = await db.Campaigns.Where(c => c.Id == campaignId).Select(c => c.CurrentVersionId).SingleAsync(cancellationToken);
        var paused = await db.Campaigns
            .Where(c => c.Id == campaignId && (c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CampaignStatus.Paused)
                .SetProperty(c => c.StatusReason, text)
                .SetProperty(c => c.StatusUtc, nowUtc), cancellationToken);
        if (paused == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        var held = await db.DispatchJobs
            .Where(j => j.CampaignVersionId == versionId
                        && (j.DispatchStatus == DispatchStatus.Queued || j.DispatchStatus == DispatchStatus.RetryScheduled))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.DispatchStatus, DispatchStatus.OnHold), cancellationToken);
        db.AuditLogs.Add(AuditEntry.Create(SystemActor, AuditActions.PausarCampana, "Campana",
            campaignId.ToString(CultureInfo.InvariantCulture), null, new { estado = CampaignStatus.Paused.ToString(), motivo = text, enEspera = held },
            nowUtc));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }
}
