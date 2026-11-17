using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Capacity;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Capacity;

/// <summary>Configuración de la sección "Capacity" (F0.11).</summary>
public sealed class CapacityOptions
{
    public const string SectionName = "Capacity";

    /// <summary>Business portfolio ID: el límite de Meta es por portafolio y lo comparten todos sus números.</summary>
    public string PortfolioId { get; set; } = "";

    /// <summary>
    /// Límite del portafolio (destinatarios únicos / 24 h). Por ahora configurado a mano; Meta lo informa en
    /// GET /{phone-number-id}?fields=whatsapp_business_manager_messaging_limit (TIER_250, TIER_2K…) y en el webhook
    /// business_capability_update.
    /// </summary>
    public int PortfolioLimit { get; set; } = 250;

    /// <summary>Porcentaje del límite que se usa (docs/ARQUITECTURA.md: hasta el 90 %).</summary>
    public int UsagePercent { get; set; } = 90;

    /// <summary>Destinatarios por 24 h reservados para los envíos manuales fuera del sistema (D-011).</summary>
    public int ManualQuota { get; set; } = 200;

    /// <summary>Espera máxima por el bloqueo de capacidad del portafolio; si vence, la vuelta no reserva.</summary>
    public int LockTimeoutMs { get; set; } = 5000;

    public int Budget => CapacityBudget.Compute(PortfolioLimit, UsagePercent, ManualQuota);
}

/// <summary>
/// Reserva atómica de capacidad por portafolio (F0.11). Varias instancias del despachador pueden reservar a la vez:
/// cada reserva toma un bloqueo de aplicación exclusivo del portafolio (sp_getapplock, dueño la transacción) solo
/// mientras cuenta y escribe; nunca durante el POST (regla 5).
/// https://learn.microsoft.com/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql
/// </summary>
public sealed class CapacityService(WaBulkDbContext db, IOptions<CapacityOptions> options)
{
    // Cuenta destinatarios únicos con reserva vigente; un número ya presente no ocupa lugar nuevo.
    // Resultado: 1 = Reserved, 2 = SameRecipient, 0 = NoCapacity, -1 = sin bloqueo a tiempo.
    private const string ReserveSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DECLARE @lock int;
        EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction',
                                   @LockTimeout = @lockTimeout;
        IF @lock < 0
        BEGIN
            ROLLBACK TRANSACTION;
            SELECT -1;
            RETURN;
        END;

        DECLARE @known bit = CASE WHEN EXISTS (
            SELECT 1 FROM CapacityReservation
            WHERE PortafolioId = @portfolio AND PhoneE164 = @phone
              AND Estado IN ('Reservada', 'Consumida') AND ExpiraUtc > @now) THEN 1 ELSE 0 END;

        DECLARE @used int = (
            SELECT COUNT(DISTINCT PhoneE164) FROM CapacityReservation
            WHERE PortafolioId = @portfolio AND Estado IN ('Reservada', 'Consumida') AND ExpiraUtc > @now);

        IF @known = 1 OR @used < @budget
        BEGIN
            INSERT INTO CapacityReservation (PortafolioId, PhoneE164, DispatchJobId, ReservadaUtc, ExpiraUtc, Estado)
            VALUES (@portfolio, @phone, @jobId, @now, DATEADD(HOUR, 24, @now), 'Reservada');
            COMMIT TRANSACTION;
            SELECT CASE WHEN @known = 1 THEN 2 ELSE 1 END;
        END
        ELSE
        BEGIN
            COMMIT TRANSACTION;
            SELECT 0;
        END;
        """;

    private CapacityOptions Options => options.Value;

    public async Task<ReserveOutcome> ReserveAsync(long jobId, string phoneE164, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = ReserveSql;
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = $"WaBulk:capacidad:{Options.PortfolioId}" });
            command.Parameters.Add(new SqlParameter("@lockTimeout", SqlDbType.Int) { Value = Options.LockTimeoutMs });
            command.Parameters.Add(new SqlParameter("@portfolio", SqlDbType.VarChar, 32) { Value = Options.PortfolioId });
            command.Parameters.Add(new SqlParameter("@phone", SqlDbType.VarChar, 16) { Value = phoneE164 });
            command.Parameters.Add(new SqlParameter("@jobId", SqlDbType.BigInt) { Value = jobId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = nowUtc });
            command.Parameters.Add(new SqlParameter("@budget", SqlDbType.Int) { Value = Options.Budget });

            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            return result switch
            {
                1 => ReserveOutcome.Reserved,
                2 => ReserveOutcome.SameRecipient,
                _ => ReserveOutcome.NoCapacity,
            };
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Libera la reserva en curso de un trabajo que no llegó a enviarse.</summary>
    public Task<int> ReleaseAsync(long jobId, CancellationToken cancellationToken) =>
        db.CapacityReservations
            .Where(r => r.DispatchJobId == jobId && r.Status == ReservationStatus.Reservada)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReservationStatus.Liberada), cancellationToken);
}
