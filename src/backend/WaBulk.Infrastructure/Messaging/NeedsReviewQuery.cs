using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>Un envío incierto que espera revisión de una persona.</summary>
public sealed record NeedsReviewItem(
    long JobId, long? CampaignVersionId, string PhoneE164, Guid AttemptId, DateTime StartedUtc, string? Wamid, string? Detail);

/// <summary>
/// Trabajos NeedsReview vencidos (F1.7, docs/ARQUITECTURA.md "Resultado de un intento"): el intento quedó
/// SubmissionUnknown y en <c>Dispatcher:NeedsReviewHours</c> (inicial 2 h) no llegó ningún webhook que lo resuelva.
/// Nunca se reenvían solos (regla 4): una persona decide si llegó o no (pantalla en F1.8; hoy, comando "revision").
/// </summary>
public static class NeedsReviewQuery
{
    public static Task<List<NeedsReviewItem>> OverdueAsync(WaBulkDbContext db, DateTime nowUtc, int hours, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var before = nowUtc.AddHours(-hours);
        return (from j in db.DispatchJobs.AsNoTracking()
                join a in db.SendAttempts.AsNoTracking() on j.Id equals a.DispatchJobId
                where j.DispatchStatus == DispatchStatus.NeedsReview && a.Status == SendAttemptStatus.SubmissionUnknown
                      && a.StartedUtc <= before
                orderby a.StartedUtc
                select new NeedsReviewItem(j.Id, j.CampaignVersionId, j.PhoneE164, a.AttemptId, a.StartedUtc, a.Wamid, a.Detail))
            .ToListAsync(cancellationToken);
    }
}
