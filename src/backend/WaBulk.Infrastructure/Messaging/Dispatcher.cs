using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>Resumen de una vuelta del despachador.</summary>
public sealed record DispatchRound(int Recovered, int Expired, int Claimed, int Sent, int LeaseLost);

/// <summary>
/// Despachador (F0.9). Una vuelta: recupera leases vencidos → marca vencidos → reclama un lote →
/// por cada trabajo: verifica lease e inserta SendAttempt (confirmado) → POST sin retry → guarda resultado y
/// estado del trabajo verificando el dueño. Nunca mantiene una transacción abierta durante el POST (regla 5).
/// Las revalidaciones de campaña, consentimiento, fatiga y capacidad llegan en F0.10, F0.11 y Fase 1.
/// </summary>
public sealed partial class Dispatcher(
    WaBulkDbContext db,
    DispatchQueue queue,
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
        var ids = await queue.ClaimBatchAsync(Owner, Options.BatchSize, Now, Options.Lease, cancellationToken);

        int sent = 0, lost = 0;
        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Los trabajos reclamados y no empezados vuelven a la cola cuando venza su lease.
                break;
            }

            switch (await ProcessJobAsync(id, cancellationToken))
            {
                case BeginOutcome.Started: sent++; break;
                case BeginOutcome.LeaseLost: lost++; break;
            }
        }

        return new DispatchRound(recovered.Count, expired, ids.Count, sent, lost);
    }

    /// <summary>Procesa un trabajo ya reclamado por este despachador.</summary>
    public async Task<BeginOutcome> ProcessJobAsync(long jobId, CancellationToken cancellationToken)
    {
        var job = await db.DispatchJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, cancellationToken);
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
            LogNotStarted(logger, job.Id, begin);
            return begin;
        }

        // Desde aquí el POST pudo ocurrir: nada de lo que sigue se cancela (el resultado se guarda siempre).
        var result = await meta.SendTemplateAsync(
            job.PhoneE164, new TemplateMessage(Options.TemplateName, Options.TemplateLanguage), attempt.AttemptId, cancellationToken);

        var status = result.Status;
        if (status == SendAttemptStatus.SubmissionUnknown
            && await queue.CurrentWamidAsync(attempt.AttemptId, CancellationToken.None) is not null)
        {
            // Llegó un webhook con este AttemptId durante el POST: Meta sí lo recibió.
            status = SendAttemptStatus.Accepted;
        }

        var transition = DispatchRetryPolicy.After(
            status, result.ErrorCode, job.Attempts + 1, job.ExpiresUtc, Now, Options.Retry, Jitter());

        var owned = await queue.CompleteAttemptAsync(
            job.Id, Owner, attempt.AttemptId, status, result.Wamid, result.HttpStatus, result.ErrorCode, result.Detail,
            transition, Now, CancellationToken.None);

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

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static double Jitter() => RandomNumberGenerator.GetInt32(1_000_000) / 1_000_000d;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease vencido del trabajo {JobId}: queda {Status}")]
    private static partial void LogRecovered(ILogger logger, long jobId, DispatchStatus status);

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
