using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WaBulk.Domain.Alerts;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Alerts;

public sealed record AlertRaised(long AlertId, bool NewAlert, bool PauseCreated);

/// <summary>
/// Freno de emergencia (F0.12): registra la alerta (tabla + log Critical) y deja el despacho en pausa global.
/// Usa SQL crudo en su propia transacción corta, serializada con sp_getapplock: así varios despachadores y el
/// procesador pueden alertar a la vez sin duplicar la alerta ni la pausa, y no se guardan a medias los cambios que
/// el procesador tiene pendientes en su DbContext.
/// </summary>
public sealed partial class AlertService(WaBulkDbContext db, TimeProvider clock, ILogger<AlertService> logger)
{
    private const string RaiseSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DECLARE @lock int;
        EXEC @lock = sp_getapplock @Resource = N'WaBulk:alertas', @LockMode = 'Exclusive', @LockOwner = 'Transaction',
                                   @LockTimeout = 10000;
        IF @lock < 0 THROW 50001, 'No se obtuvo el bloqueo de alertas', 1;

        DECLARE @alertId bigint = (SELECT Id FROM Alert WHERE Tipo = @type AND Clave = @key AND ResueltaUtc IS NULL);
        DECLARE @newAlert bit = 0, @pauseCreated bit = 0;

        IF @alertId IS NULL
        BEGIN
            INSERT INTO Alert (Tipo, Clave, Mensaje, Detalle, Origen, Ocurrencias, CreadaUtc, UltimaUtc)
            VALUES (@type, @key, @message, @detail, @source, 1, @now, @now);
            SET @alertId = SCOPE_IDENTITY();
            SET @newAlert = 1;
        END
        ELSE
            UPDATE Alert SET Ocurrencias = Ocurrencias + 1, UltimaUtc = @now WHERE Id = @alertId;

        IF NOT EXISTS (SELECT 1 FROM DispatchPause WHERE Alcance = 'Global' AND LevantadaUtc IS NULL)
        BEGIN
            INSERT INTO DispatchPause (Alcance, Motivo, AlertId, DesdeUtc) VALUES ('Global', @type, @alertId, @now);
            SET @pauseCreated = 1;
        END;

        COMMIT TRANSACTION;
        SELECT @alertId, @newAlert, @pauseCreated;
        """;

    /// <summary>Registra la alerta y pausa el despacho. Idempotente: repetirla solo suma ocurrencias.</summary>
    public async Task<AlertRaised> RaiseAsync(
        AlertType type, string key, string message, string? detail, string source, CancellationToken cancellationToken)
    {
        AlertRaised raised;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = RaiseSql;
            command.Parameters.Add(new SqlParameter("@type", SqlDbType.VarChar, 30) { Value = type.ToString() });
            command.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 200) { Value = key });
            command.Parameters.Add(new SqlParameter("@message", SqlDbType.NVarChar, 500) { Value = message });
            command.Parameters.Add(new SqlParameter("@detail", SqlDbType.NVarChar, -1) { Value = (object?)detail ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@source", SqlDbType.VarChar, 30) { Value = source });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = clock.GetUtcNow().UtcDateTime });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            raised = new AlertRaised(reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        if (raised.NewAlert)
        {
            LogAlert(logger, type, key, message, source);
        }

        if (raised.PauseCreated)
        {
            LogPaused(logger, type, raised.AlertId);
        }

        return raised;
    }

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) =>
        db.DispatchPauses.AsNoTracking().AnyAsync(p => p.Scope == PauseScopes.Global && p.LiftedUtc == null, cancellationToken);

    /// <summary>
    /// Levanta la pausa global y resuelve las alertas abiertas (las revisó una persona). Si el problema sigue
    /// (por ejemplo, el token aún es inválido), el siguiente envío vuelve a alertar y pausar.
    /// </summary>
    public async Task<(int Pauses, int Alerts)> ResumeAsync(string liftedBy, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var pauses = await db.DispatchPauses.Where(p => p.LiftedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LiftedUtc, now).SetProperty(p => p.LiftedBy, liftedBy), cancellationToken);
        var alerts = await db.Alerts.Where(a => a.ResolvedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvedUtc, now).SetProperty(a => a.ResolvedBy, liftedBy), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        LogResumed(logger, liftedBy, pauses, alerts);
        return (pauses, alerts);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "ALERTA {Type} ({Key}) desde {Source}: {Message}")]
    private static partial void LogAlert(ILogger logger, AlertType type, string key, string message, string source);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Despacho en PAUSA GLOBAL por {Type} (alerta {AlertId}). Levantar con: reanudar --por <nombre>")]
    private static partial void LogPaused(ILogger logger, AlertType type, long alertId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pausa levantada por {LiftedBy}: {Pauses} pausas, {Alerts} alertas resueltas")]
    private static partial void LogResumed(ILogger logger, string liftedBy, int pauses, int alerts);
}
