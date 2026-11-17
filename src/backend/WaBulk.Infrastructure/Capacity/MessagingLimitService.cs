using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Capacity;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Capacity;

internal sealed class MessagingLimitConfiguration : IEntityTypeConfiguration<MessagingLimit>
{
    public void Configure(EntityTypeBuilder<MessagingLimit> builder)
    {
        builder.ToTable("MessagingLimit");
        builder.HasKey(x => x.PortfolioId);
        builder.Property(x => x.PortfolioId).HasColumnName("PortafolioId").HasMaxLength(32).IsUnicode(false);
        builder.Property(x => x.Limit).HasColumnName("Limite");
        builder.Property(x => x.MetaValue).HasColumnName("ValorMeta").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.Source).HasColumnName("Origen").HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.UpdatedUtc).HasColumnName("ActualizadoUtc");
    }
}

/// <summary>
/// Límite real de Meta (F1.7, D-026). <see cref="Apply"/> deja el cambio en el contexto (el procesador de webhooks guarda
/// todo junto); <see cref="RefreshFromApiAsync"/> lo consulta a Meta y guarda. La capacidad lo usa en lugar de
/// Capacity:PortfolioLimit cuando existe.
/// </summary>
public sealed partial class MessagingLimitService(
    WaBulkDbContext db, IOptions<CapacityOptions> options, TimeProvider clock, ILogger<MessagingLimitService> logger)
{
    /// <summary>Registra el valor de Meta. Devuelve false si no se pudo interpretar (TIER_NOT_SET, vacío…).</summary>
    public async Task<bool> ApplyAsync(string? metaValue, string source, CancellationToken cancellationToken)
    {
        if (MessagingTiers.Parse(metaValue) is not { } limit)
        {
            return false;
        }

        var portfolio = options.Value.PortfolioId;
        var row = db.MessagingLimits.Local.FirstOrDefault(m => m.PortfolioId == portfolio)
                  ?? await db.MessagingLimits.FirstOrDefaultAsync(m => m.PortfolioId == portfolio, cancellationToken);
        if (row is null)
        {
            row = new MessagingLimit { PortfolioId = portfolio, MetaValue = metaValue!, Source = source };
            db.MessagingLimits.Add(row);
        }

        if (row.Limit != limit)
        {
            LogChanged(logger, portfolio, row.Limit, limit, source);
        }

        row.Limit = limit;
        row.MetaValue = metaValue!.Trim();
        row.Source = source;
        row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        return true;
    }

    /// <summary>GET del límite a Meta y guardado. Devuelve el valor de Meta, o null si no se pudo leer.</summary>
    public async Task<string?> RefreshFromApiAsync(MetaWhatsAppClient meta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meta);
        string? value;
        try
        {
            value = await meta.GetMessagingLimitAsync(cancellationToken);
        }
        catch (MetaApiException ex)
        {
            LogRefreshFailed(logger, ex.Message);
            return null;
        }

        if (await ApplyAsync(value, "Api", cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Límite de mensajería del portafolio {Portfolio}: {Old} → {New} ({Source})")]
    private static partial void LogChanged(ILogger logger, string portfolio, int old, int @new, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo leer el límite de mensajería de Meta: {Error}")]
    private static partial void LogRefreshFailed(ILogger logger, string error);
}
