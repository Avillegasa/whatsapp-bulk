using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Alerts;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Capacity;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Campaigns;
using WaBulk.Infrastructure.Capacity;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>Resumen de una vuelta del despachador.</summary>
public sealed record DispatchRound(
    int Recovered, int Expired, int Claimed, int Sent, int LeaseLost, int Skipped, int NoCapacity = 0, bool Paused = false, int Held = 0,
    bool OutsideHours = false);

/// <summary>
/// Despachador (F0.9). Una vuelta: recupera leases vencidos → marca vencidos → reclama un lote →
/// por cada trabajo: verifica lease e inserta SendAttempt (confirmado) → POST sin retry → guarda resultado y
/// estado del trabajo verificando el dueño. Nunca mantiene una transacción abierta durante el POST (regla 5).
/// Antes de cada envío revisa las bajas de empresa y de plataforma (F0.10, regla 8): si no es elegible, Skipped sin POST.
/// Después reserva capacidad del portafolio (F0.11): sin lugar, el trabajo se pospone (RetryScheduled, sin sumar intento)
/// y la vuelta sigue con el resto del lote, porque un trabajo a un número ya contado no necesita lugar nuevo.
/// Freno de emergencia (F0.12): con una pausa global activa no reclama; si un envío recibe 190, 131042 o 132015,
/// alerta, pausa y devuelve a la cola lo que quedaba del lote.
/// Campañas (F1.6): al inicio de cada vuelta avanza el ciclo de vida de las campañas; antes de cada envío revisa la
/// campaña del trabajo (cancelada o versión reemplazada → Skipped; en pausa → OnHold) y toma la plantilla y los
/// parámetros de la versión.
/// F1.7: fuera del horario permitido no reclama (y suelta lo que quedaba del lote); tope de promociones por contacto en
/// 7 días (Skipped LimiteFrecuencia, D-031); tasa por número (SendRateLimiter) antes de cada POST; plantilla APPROVED
/// al enviar y errores 132000–132016 (salvo 132015) → pausa de la campaña.
/// </summary>
public sealed partial class Dispatcher(
    WaBulkDbContext db,
    DispatchQueue queue,
    ConsentService consent,
    CapacityService capacity,
    AlertService alerts,
    CampaignLifecycle lifecycle,
    SendRateLimiter rateLimiter,
    IOptions<FrequencyOptions> frequency,
    MetaWhatsAppClient meta,
    IOptions<DispatcherOptions> options,
    DispatcherIdentity identity,
    TimeProvider clock,
    ILogger<Dispatcher> logger)
{
    private DispatcherOptions Options => options.Value;

    public string Owner => identity.Owner;

    public async Task<DispatchRound> RunOnceAsync(CancellationToken cancellationToken)
    {
        var recovered = await queue.RecoverExpiredLeasesAsync(Now, cancellationToken);
        foreach (var job in recovered)
        {
            LogRecovered(logger, job.JobId, job.Status);
        }

        var expired = await queue.ExpireOverdueAsync(Now, cancellationToken);
        await lifecycle.AdvanceAsync(Now, cancellationToken);

        if (await alerts.IsPausedAsync(cancellationToken))
        {
            return new DispatchRound(recovered.Count, expired, 0, 0, 0, 0, Paused: true);
        }

        var window = Options.Window;
        if (!window.IsOpen(Now))
        {
            return new DispatchRound(recovered.Count, expired, 0, 0, 0, 0, OutsideHours: true);
        }

        var ids = await queue.ClaimBatchAsync(Owner, Options.BatchSize, Now, Options.Lease, cancellationToken);

        int sent = 0, lost = 0, skipped = 0, noCapacity = 0, held = 0;
        var paused = false;
        for (var i = 0; i < ids.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Los trabajos reclamados y no empezados vuelven a la cola cuando venza su lease.
                break;
            }

            // La pausa puede llegar a mitad del lote (este despachador, otro o el procesador de webhooks), y el horario
            // puede cerrar a mitad del lote: lo que no empezó vuelve a la cola.
            var closed = !window.IsOpen(Now);
            if (closed || await alerts.IsPausedAsync(cancellationToken))
            {
                foreach (var pending in ids.Skip(i))
                {
                    await queue.ReleaseClaimAsync(pending, Owner, cancellationToken);
                }

                paused = !closed;
                if (closed)
                {
                    return new DispatchRound(recovered.Count, expired, ids.Count, sent, lost, skipped, noCapacity, false, held, OutsideHours: true);
                }

                break;
            }

            switch (await ProcessJobAsync(ids[i], cancellationToken))
            {
                case BeginOutcome.Started: sent++; break;
                case BeginOutcome.LeaseLost: lost++; break;
                case BeginOutcome.Skipped: skipped++; break;
                case BeginOutcome.NoCapacity: noCapacity++; break;
                case BeginOutcome.Held: held++; break;
            }
        }

        if (noCapacity > 0)
        {
            LogNoCapacity(logger, noCapacity, Options.NoCapacityRetryMinutes);
        }

        return new DispatchRound(recovered.Count, expired, ids.Count, sent, lost, skipped, noCapacity, paused, held);
    }

    /// <summary>Procesa un trabajo ya reclamado por este despachador.</summary>
    public async Task<BeginOutcome> ProcessJobAsync(long jobId, CancellationToken cancellationToken)
    {
        var job = await db.DispatchJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, cancellationToken);

        // Campaña del trabajo (F1.6): una cancelación, una versión nueva o una pausa que llegó después de encolar gana.
        var campaign = await (from v in db.CampaignVersions.AsNoTracking()
                              join c in db.Campaigns.AsNoTracking() on v.CampaignId equals c.Id
                              join t in db.Templates.AsNoTracking() on v.TemplateId equals t.Id
                              where v.Id == job.CampaignVersionId
                              select new { Version = v, c.Status, c.CurrentVersionId, TemplateStatus = t.Status })
            .SingleOrDefaultAsync(cancellationToken);
        var campaignSkip = campaign switch
        {
            null => SkipReasons.SinCampania,
            { Status: CampaignStatus.Cancelled } => SkipReasons.CampanaCancelada,
            _ when campaign.CurrentVersionId != campaign.Version.Id => SkipReasons.VersionReemplazada,
            _ => null,
        };
        if (campaignSkip is not null)
        {
            var skippedByCampaign = await queue.SkipAsync(job.Id, Owner, campaignSkip, cancellationToken);
            LogSkipped(logger, job.Id, campaignSkip);
            return skippedByCampaign ? BeginOutcome.Skipped : BeginOutcome.LeaseLost;
        }

        if (!CampaignRules.IsSending(campaign!.Status))
        {
            var held = await queue.HoldAsync(job.Id, Owner, cancellationToken);
            LogHeld(logger, job.Id, campaign.Status);
            return held ? BeginOutcome.Held : BeginOutcome.LeaseLost;
        }

        var version = campaign.Version;

        // Plantilla APPROVED al enviar (F1.7): si Meta la rechazó, pausó o desactivó después de aprobar la campaña, la
        // campaña se pausa (lo pendiente queda OnHold) y este trabajo espera sin POST.
        if (campaign.TemplateStatus != TemplateStatuses.Approved)
        {
            await lifecycle.PauseForTemplateAsync(version.CampaignId,
                $"La plantilla {version.TemplateName} ({version.Language}) está {campaign.TemplateStatus}: revisar antes de reanudar.",
                Now, CancellationToken.None);
            var heldByTemplate = await queue.HoldAsync(job.Id, Owner, cancellationToken);
            LogHeld(logger, job.Id, CampaignStatus.Paused);
            return heldByTemplate ? BeginOutcome.Held : BeginOutcome.LeaseLost;
        }

        var message = new TemplateMessage(version.TemplateName, version.Language,
            [.. CampaignRules.DeserializeJobParameters(job.Parameters).Select(p => new TemplateParameter(p.Name, p.Value))],
            !string.Equals(version.ParameterFormat, "POSITIONAL", StringComparison.OrdinalIgnoreCase));

        // Revisión de bajas al enviar: una baja que llegó después de encolar gana (regla 8).
        var eligibility = await consent.EvaluateAsync(job.PhoneE164, cancellationToken);
        if (!eligibility.Eligible)
        {
            var skippedNow = await queue.SkipAsync(job.Id, Owner, eligibility.SkipReason!, cancellationToken);
            LogSkipped(logger, job.Id, eligibility.SkipReason);
            return skippedNow ? BeginOutcome.Skipped : BeginOutcome.LeaseLost;
        }

        // Tope semanal por contacto (F1.7, D-031): lo que el texto de la oferta prometió.
        if (await PromotionsThisWeekAsync(job) >= frequency.Value.MaxPromotionsPerWeek)
        {
            var capped = await queue.SkipAsync(job.Id, Owner, SkipReasons.LimiteFrecuencia, cancellationToken);
            LogSkipped(logger, job.Id, SkipReasons.LimiteFrecuencia);
            return capped ? BeginOutcome.Skipped : BeginOutcome.LeaseLost;
        }

        // Tasa por número (F1.7): espera su turno antes de reservar y enviar.
        await rateLimiter.WaitAsync(cancellationToken);

        // Capacidad del portafolio (F0.11): reserva atómica antes del intento; un número ya contado no ocupa lugar.
        if (await capacity.ReserveAsync(job.Id, job.PhoneE164, Now, cancellationToken) == ReserveOutcome.NoCapacity)
        {
            await queue.DeferAsync(job.Id, Owner, Now.AddMinutes(Options.NoCapacityRetryMinutes), cancellationToken);
            return BeginOutcome.NoCapacity;
        }

        var attempt = new SendAttempt
        {
            AttemptId = Guid.CreateVersion7(),
            DispatchJobId = job.Id,
            StartedUtc = Now,
            Status = SendAttemptStatus.Submitting,
        };

        var begin = await queue.BeginAttemptAsync(job.Id, Owner, attempt, Now, Options.Lease, cancellationToken);
        if (begin != BeginOutcome.Started)
        {
            await capacity.ReleaseAsync(job.Id, cancellationToken); // No hubo POST: el lugar queda libre.
            LogNotStarted(logger, job.Id, begin);
            return begin;
        }

        // Desde aquí el POST pudo ocurrir: nada de lo que sigue se cancela (el resultado se guarda siempre).
        var result = await meta.SendTemplateAsync(job.PhoneE164, message, attempt.AttemptId, cancellationToken);

        var status = result.Status;
        if (status == SendAttemptStatus.SubmissionUnknown
            && await queue.CurrentWamidAsync(attempt.AttemptId, CancellationToken.None) is not null)
        {
            // Llegó un webhook con este AttemptId durante el POST: Meta sí lo recibió.
            status = SendAttemptStatus.Accepted;
        }

        var transition = DispatchRetryPolicy.After(
            status, result.ErrorCode, job.Attempts + 1, job.ExpiresUtc, Now, Options.Retry, Jitter());

        PlatformPreference? platformStop = result.ErrorCode == PlatformStopErrorCode
            ? new PlatformPreference
            {
                PhoneE164 = job.PhoneE164,
                Value = PlatformPreferenceValue.Stop,
                Origin = PlatformPreferenceOrigin.Error131050,
                RecordedUtc = Now,
            }
            : null;

        var owned = await queue.CompleteAttemptAsync(
            job.Id, Owner, attempt.AttemptId, status, result.Wamid, result.HttpStatus, result.ErrorCode, result.Detail,
            transition, Now, CancellationToken.None, platformStop);

        if (status == SendAttemptStatus.Rejected && EmergencyBrake.ForErrorCode(result.ErrorCode) is { } alertType)
        {
            var key = alertType == AlertType.PlantillaPausada ? $"{version.TemplateName}:{version.Language}" : "global";
            await alerts.RaiseAsync(alertType, key, $"Error {result.ErrorCode} al enviar el trabajo {job.Id}", result.Detail,
                "Despachador", CancellationToken.None);
        }

        // 132000–132016 (salvo 132015, que pausa todo): problema de esta plantilla → pausa solo de esta campaña (F1.7).
        // 132016 = plantilla desactivada para siempre: hay que crear otra y una versión nueva de la campaña.
        if (status == SendAttemptStatus.Rejected && IsCampaignTemplateError(result.ErrorCode))
        {
            await lifecycle.PauseForTemplateAsync(version.CampaignId,
                $"Error {result.ErrorCode} de la plantilla {version.TemplateName} al enviar: {result.Detail}"
                + (result.ErrorCode == 132016 ? " La plantilla quedó desactivada: crear otra y una versión nueva." : ""),
                Now, CancellationToken.None);
        }

        if (owned)
        {
            LogCompleted(logger, job.Id, attempt.AttemptId, status, result.ErrorCode, transition.Status, transition.NextAttemptUtc);
        }
        else
        {
            LogCompletedWithoutLease(logger, job.Id, attempt.AttemptId, status);
        }

        return BeginOutcome.Started;
    }

    /// <summary>
    /// Promociones enviadas al contacto en los últimos 7 días: trabajos de campaña (no send-test) con un intento aceptado,
    /// incierto o en curso (lo incierto cuenta: puede haber llegado). Excluye el trabajo actual (un reintento no suma).
    /// </summary>
    private Task<int> PromotionsThisWeekAsync(DispatchJob job)
    {
        var since = Now.AddDays(-7);
        return (from a in db.SendAttempts.AsNoTracking()
                join j in db.DispatchJobs.AsNoTracking() on a.DispatchJobId equals j.Id
                where j.ContactId == job.ContactId && j.Id != job.Id && j.CampaignVersionId != null && a.StartedUtc >= since
                      && (a.Status == SendAttemptStatus.Accepted || a.Status == SendAttemptStatus.SubmissionUnknown
                          || a.Status == SendAttemptStatus.Submitting)
                select j.Id).Distinct().CountAsync(CancellationToken.None);
    }

    /// <summary>
    /// Errores de plantilla que pausan la campaña (docs/ARQUITECTURA.md "Errores": 132000–132016 → pausar campaña). 132015
    /// queda en el freno global (F0.12).
    /// </summary>
    public static bool IsCampaignTemplateError(int? code) => code is >= 132000 and <= 132016 and not 132015;

    /// <summary>131050: "This recipient has chosen to stop receiving marketing messages" (no reintentar).</summary>
    public const int PlatformStopErrorCode = 131050;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static double Jitter() => RandomNumberGenerator.GetInt32(1_000_000) / 1_000_000d;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease vencido del trabajo {JobId}: queda {Status}")]
    private static partial void LogRecovered(ILogger logger, long jobId, DispatchStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sin capacidad del portafolio: {Count} trabajos se posponen {Minutes} min")]
    private static partial void LogNoCapacity(ILogger logger, int count, int minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trabajo {JobId} omitido sin POST: {Reason}")]
    private static partial void LogSkipped(ILogger logger, long jobId, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trabajo {JobId} en espera: su campaña está {Status}")]
    private static partial void LogHeld(ILogger logger, long jobId, CampaignStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trabajo {JobId} no enviado: {Outcome}")]
    private static partial void LogNotStarted(ILogger logger, long jobId, BeginOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Trabajo {JobId} intento {AttemptId}: {Status} código={ErrorCode} → {JobStatus} próximo={NextAttemptUtc}")]
    private static partial void LogCompleted(ILogger logger, long jobId, Guid attemptId, SendAttemptStatus status, int? errorCode,
        DispatchStatus jobStatus, DateTime? nextAttemptUtc);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Trabajo {JobId} intento {AttemptId}: {Status} guardado, pero el lease se había perdido (el trabajo no se tocó)")]
    private static partial void LogCompletedWithoutLease(ILogger logger, long jobId, Guid attemptId, SendAttemptStatus status);
}

/// <summary>Identidad del despachador para LockedBy; una por proceso (o la de Dispatcher:WorkerId).</summary>
public sealed class DispatcherIdentity(IOptions<DispatcherOptions> options)
{
    public string Owner { get; } = string.IsNullOrWhiteSpace(options.Value.WorkerId)
        ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}"
        : options.Value.WorkerId;
}
