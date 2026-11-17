using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WaBulk.Domain.Capacity;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>Resultado de empezar un trabajo reclamado.</summary>
public enum BeginOutcome
{
    /// <summary>Lease verificado y renovado; SendAttempt confirmado en Submitting: se puede hacer el POST.</summary>
    Started,
    /// <summary>El worker ya no es dueño del lease (venció o lo recuperó otro): no se envía.</summary>
    LeaseLost,
    /// <summary>El trabajo venció (ExpiraUtc) mientras estaba reclamado: queda Expired sin POST.</summary>
    Expired,
    /// <summary>El contacto no es elegible (baja de empresa o de plataforma, sin consentimiento): Skipped sin POST.</summary>
    Skipped,
    /// <summary>Sin capacidad del portafolio (F0.11): el trabajo se reprograma sin POST y sin sumar intento.</summary>
    NoCapacity,
    /// <summary>La campaña no está enviando (en pausa): el trabajo queda OnHold sin POST (F1.6).</summary>
    Held,
}

public sealed record RecoveredJob(long JobId, DispatchStatus Status);

/// <summary>
/// Cola sobre DispatchJob (regla 5, D-013). Solo SQL Server: el reclamo y la recuperación usan SQL crudo con
/// UPDLOCK + READPAST para que varios despachadores tomen filas distintas sin esperarse.
/// Todas las sentencias usan @now del TimeProvider (no SYSUTCDATETIME) para poder probar el tiempo.
/// Ninguna transacción de esta clase abarca una llamada HTTP.
/// https://learn.microsoft.com/sql/t-sql/queries/hints-transact-sql-table (READPAST: "primarily used to reduce
/// locking contention when implementing a work queue"). La doc dice que READPAST no se admite con READ_COMMITTED_SNAPSHOT ON
/// en READ COMMITTED; probado en SQL Server 2022 (F0.9): UPDATE con UPDLOCK, READPAST, ROWLOCK funciona con RCSI ON y OFF.
/// Revisar si producción (D-006) usa Azure SQL, que trae RCSI ON por defecto.
/// </summary>
public sealed class DispatchQueue(WaBulkDbContext db)
{
    // Prioridad mayor primero; a igual prioridad, el más antiguo. El índice filtrado IX_DispatchJob_Reclamo
    // (Prioridad DESC, Id) da ese orden sin ordenar: TOP (@batch) se detiene en @batch filas y solo esas quedan
    // bloqueadas. El IN con literales repite el filtro del índice para que el hint INDEX sea válido.
    private const string ClaimSql = """
        WITH next AS (
            SELECT TOP (@batch) Id, EstadoDespacho, LockedBy, LeaseUntilUtc
            FROM DispatchJob WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_DispatchJob_Reclamo))
            WHERE EstadoDespacho IN ('Queued', 'RetryScheduled')
              AND ((EstadoDespacho = 'Queued' AND ProgramadoUtc <= @now)
                OR (EstadoDespacho = 'RetryScheduled' AND ProximoIntentoUtc <= @now))
              AND ExpiraUtc > @now
            ORDER BY Prioridad DESC, Id
        )
        UPDATE next
        SET EstadoDespacho = 'Claimed', LockedBy = @owner, LeaseUntilUtc = @lease
        OUTPUT inserted.Id;
        """;

    // Lease vencido (docs/ARQUITECTURA.md "Estados"):
    // - intento en Submitting con prueba de que Meta lo recibió (wamid o MessageEvent con su AttemptId) → Accepted / Submitted;
    // - intento en Submitting sin prueba → SubmissionUnknown / NeedsReview (nunca se reenvía, regla 4);
    // - sin intento pendiente (caída antes del POST) → vuelve a Queued.
    private const string RecoverSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DECLARE @ids TABLE (Id bigint PRIMARY KEY);
        DECLARE @out TABLE (Id bigint, Estado varchar(20));

        INSERT INTO @ids (Id)
        SELECT Id FROM DispatchJob WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE EstadoDespacho = 'Claimed' AND LeaseUntilUtc < @now;

        UPDATE a
        SET Estado = CASE WHEN a.Wamid IS NOT NULL
                            OR EXISTS (SELECT 1 FROM MessageEvent e WHERE e.AttemptId = a.AttemptId)
                          THEN 'Accepted' ELSE 'SubmissionUnknown' END,
            FinUtc = @now,
            Detalle = CONCAT('Lease vencido en Submitting (', j.LockedBy, ').', ' ' + a.Detalle)
        FROM SendAttempt a
        JOIN DispatchJob j ON j.Id = a.DispatchJobId
        JOIN @ids i ON i.Id = j.Id
        WHERE a.Estado = 'Submitting';

        UPDATE j
        SET EstadoDespacho = CASE
                WHEN EXISTS (SELECT 1 FROM SendAttempt a WHERE a.DispatchJobId = j.Id AND a.Estado = 'Accepted') THEN 'Submitted'
                WHEN EXISTS (SELECT 1 FROM SendAttempt a WHERE a.DispatchJobId = j.Id AND a.Estado = 'SubmissionUnknown') THEN 'NeedsReview'
                ELSE 'Queued' END,
            LockedBy = NULL,
            LeaseUntilUtc = NULL
        OUTPUT inserted.Id, inserted.EstadoDespacho INTO @out
        FROM DispatchJob j
        JOIN @ids i ON i.Id = j.Id;

        -- Capacidad (F0.11): vuelve a la cola → se libera; enviado o incierto → cuenta (por prudencia).
        UPDATE r
        SET Estado = CASE WHEN o.Estado = 'Queued' THEN 'Liberada' ELSE 'Consumida' END
        FROM CapacityReservation r
        JOIN @out o ON o.Id = r.DispatchJobId
        WHERE r.Estado = 'Reservada';

        COMMIT TRANSACTION;

        SELECT Id, Estado FROM @out;
        """;

    private const string ExpireSql = """
        UPDATE DispatchJob
        SET EstadoDespacho = 'Expired'
        WHERE EstadoDespacho IN ('Queued', 'RetryScheduled', 'OnHold') AND ExpiraUtc <= @now;
        """;

    /// <summary>Reclama hasta <paramref name="batchSize"/> trabajos listos en una sola sentencia (transacción corta).</summary>
    public async Task<IReadOnlyList<long>> ClaimBatchAsync(
        string owner, int batchSize, DateTime nowUtc, TimeSpan lease, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(ClaimSql, r => r.GetInt64(0), cancellationToken,
            ("@batch", batchSize, DbType.Int32),
            ("@now", nowUtc, DbType.DateTime2),
            ("@owner", owner, DbType.String),
            ("@lease", nowUtc + lease, DbType.DateTime2));
        return rows;
    }

    /// <summary>Libera los trabajos con lease vencido según el estado de su intento.</summary>
    public async Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(DateTime nowUtc, CancellationToken cancellationToken) =>
        await QueryAsync(RecoverSql,
            r => new RecoveredJob(r.GetInt64(0), Enum.Parse<DispatchStatus>(r.GetString(1))),
            cancellationToken,
            ("@now", nowUtc, DbType.DateTime2));

    /// <summary>
    /// Marca Expired los trabajos en cola o en espera (campaña en pausa, F1.6) cuyo ExpiraUtc ya pasó: nunca se envían ni
    /// reintentan después (regla 9).
    /// </summary>
    public Task<int> ExpireOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(ExpireSql, [Parameter("@now", nowUtc, DbType.DateTime2)], cancellationToken);

    /// <summary>
    /// Verifica que el worker sigue siendo dueño del lease (y que no venció), renueva el lease, suma el intento e
    /// inserta el SendAttempt en Submitting, todo en una transacción confirmada ANTES del POST (D-010, regla 3).
    /// </summary>
    public async Task<BeginOutcome> BeginAttemptAsync(
        long jobId, string owner, SendAttempt attempt, DateTime nowUtc, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var expired = await db.DispatchJobs
            .Where(j => j.Id == jobId && j.LockedBy == owner && j.DispatchStatus == DispatchStatus.Claimed
                        && j.LeaseUntilUtc > nowUtc && j.ExpiresUtc <= nowUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.DispatchStatus, DispatchStatus.Expired)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LeaseUntilUtc, (DateTime?)null), cancellationToken);
        if (expired == 1)
        {
            await tx.CommitAsync(cancellationToken);
            return BeginOutcome.Expired;
        }

        var owned = await db.DispatchJobs
            .Where(j => j.Id == jobId && j.LockedBy == owner && j.DispatchStatus == DispatchStatus.Claimed
                        && j.LeaseUntilUtc > nowUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.LeaseUntilUtc, nowUtc + lease), cancellationToken);
        if (owned == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return BeginOutcome.LeaseLost;
        }

        db.SendAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return BeginOutcome.Started;
    }

    /// <summary>Devuelve a Queued un trabajo reclamado por este worker y no empezado (pausa global, F0.12).</summary>
    public async Task<bool> ReleaseClaimAsync(long jobId, string owner, CancellationToken cancellationToken) =>
        await db.DispatchJobs
            .Where(j => j.Id == jobId && j.LockedBy == owner && j.DispatchStatus == DispatchStatus.Claimed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.DispatchStatus, DispatchStatus.Queued)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LeaseUntilUtc, (DateTime?)null), cancellationToken) == 1;

    /// <summary>
    /// Pospone un trabajo reclamado por este worker que no consiguió capacidad: RetryScheduled hasta
    /// <paramref name="nextAttemptUtc"/>, sin sumar intento (no hubo POST). Así no se vuelve a reclamar en cada vuelta
    /// y los trabajos que siguen en la cola (por ejemplo, a un número ya contado) pueden salir.
    /// </summary>
    public async Task<bool> DeferAsync(long jobId, string owner, DateTime nextAttemptUtc, CancellationToken cancellationToken) =>
        await db.DispatchJobs
            .Where(j => j.Id == jobId && j.LockedBy == owner && j.DispatchStatus == DispatchStatus.Claimed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.DispatchStatus, DispatchStatus.RetryScheduled)
                .SetProperty(j => j.NextAttemptUtc, nextAttemptUtc)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LeaseUntilUtc, (DateTime?)null), cancellationToken) == 1;

    /// <summary>Deja OnHold un trabajo reclamado por este worker cuya campaña está en pausa (F1.6). Sin POST ni intento.</summary>
    public async Task<bool> HoldAsync(long jobId, string owner, CancellationToken cancellationToken) =>
        await db.DispatchJobs
            .Where(j => j.Id == jobId && j.LockedBy == owner && j.DispatchStatus == DispatchStatus.Claimed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.DispatchStatus, DispatchStatus.OnHold)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LeaseUntilUtc, (DateTime?)null), cancellationToken) == 1;

    /// <summary>Marca Skipped con su motivo un trabajo reclamado por este worker (revisión de bajas al enviar, F0.10).</summary>
    public async Task<bool> SkipAsync(long jobId, string owner, string reason, CancellationToken cancellationToken) =>
        await db.DispatchJobs
            .Where(j => j.Id == jobId && j.LockedBy == owner && j.DispatchStatus == DispatchStatus.Claimed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.DispatchStatus, DispatchStatus.Skipped)
                .SetProperty(j => j.SkipReason, reason)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LeaseUntilUtc, (DateTime?)null), cancellationToken) == 1;

    /// <summary>
    /// Guarda el resultado del intento y el nuevo estado del trabajo en una transacción.
    /// El trabajo solo se actualiza si el worker sigue siendo dueño (LockedBy y Claimed). Si perdió el lease, la
    /// recuperación ya dejó el intento en SubmissionUnknown y el trabajo en NeedsReview: el resultado real se guarda
    /// igual en el intento (es un hecho) y, si Meta aceptó, el trabajo pasa de NeedsReview a Submitted.
    /// Devuelve false si el lease se había perdido.
    /// </summary>
    public async Task<bool> CompleteAttemptAsync(
        long jobId, string owner, Guid attemptId, SendAttemptStatus status, string? wamid, int? httpStatus, int? errorCode,
        string? detail, DispatchTransition transition, DateTime nowUtc, CancellationToken cancellationToken,
        PlatformPreference? platformPreference = null)
    {
        ArgumentNullException.ThrowIfNull(transition);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        if (platformPreference is not null)
        {
            // 131050: el contacto detuvo el marketing en WhatsApp; se registra junto con el resultado (F0.10).
            db.PlatformPreferences.Add(platformPreference);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        // COALESCE: si el procesador ya guardó el wamid de un webhook, no se pierde.
        await db.SendAttempts
            .Where(a => a.AttemptId == attemptId
                        && (a.Status == SendAttemptStatus.Submitting || a.Status == SendAttemptStatus.SubmissionUnknown))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.Wamid, a => wamid ?? a.Wamid)
                .SetProperty(a => a.HttpStatus, httpStatus)
                .SetProperty(a => a.ErrorCode, errorCode)
                .SetProperty(a => a.Detail, detail)
                .SetProperty(a => a.FinishedUtc, nowUtc), cancellationToken);

        // Capacidad (F0.11): aceptado o incierto consume el lugar; rechazado lo libera. Es un hecho: no depende del lease.
        var reservation = status == SendAttemptStatus.Rejected ? ReservationStatus.Liberada : ReservationStatus.Consumida;
        await db.CapacityReservations
            .Where(r => r.DispatchJobId == jobId && r.Status == ReservationStatus.Reservada)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, reservation), cancellationToken);

        var owned = await db.DispatchJobs
            .Where(j => j.Id == jobId && j.LockedBy == owner && j.DispatchStatus == DispatchStatus.Claimed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.DispatchStatus, transition.Status)
                .SetProperty(j => j.NextAttemptUtc, transition.NextAttemptUtc)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LeaseUntilUtc, (DateTime?)null), cancellationToken);

        if (owned == 0 && status == SendAttemptStatus.Accepted)
        {
            await db.DispatchJobs
                .Where(j => j.Id == jobId && j.DispatchStatus == DispatchStatus.NeedsReview)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.DispatchStatus, DispatchStatus.Submitted), cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return owned == 1;
    }

    /// <summary>Lee el wamid que el procesador pudo haber guardado durante el POST (webhook antes que la respuesta).</summary>
    public Task<string?> CurrentWamidAsync(Guid attemptId, CancellationToken cancellationToken) =>
        db.SendAttempts.AsNoTracking().Where(a => a.AttemptId == attemptId).Select(a => a.Wamid).SingleAsync(cancellationToken);

    private async Task<List<T>> QueryAsync<T>(
        string sql, Func<DbDataReader, T> map, CancellationToken cancellationToken, params (string Name, object Value, DbType Type)[] parameters)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var (name, value, type) in parameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = name;
                p.Value = value;
                p.DbType = type;
                command.Parameters.Add(p);
            }

            var result = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(map(reader));
            }

            return result;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static SqlParameter Parameter(string name, object value, DbType type) =>
        new(name, value) { DbType = type };
}
