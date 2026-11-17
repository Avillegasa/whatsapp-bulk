using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Audit;

namespace WaBulk.Infrastructure.Audit;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLog");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.User).HasColumnName("Usuario").HasMaxLength(256);
        builder.Property(x => x.Action).HasColumnName("Accion").HasMaxLength(50).IsUnicode(false);
        builder.Property(x => x.Entity).HasColumnName("Entidad").HasMaxLength(50).IsUnicode(false);
        builder.Property(x => x.EntityId).HasColumnName("EntidadId").HasMaxLength(100);
        builder.Property(x => x.Before).HasColumnName("Antes");
        builder.Property(x => x.After).HasColumnName("Despues");
        builder.Property(x => x.AtUtc).HasColumnName("FechaUtc");

        // Consultas típicas: historial de una entidad y lo más reciente primero.
        builder.HasIndex(x => new { x.Entity, x.EntityId, x.AtUtc }).HasDatabaseName("IX_AuditLog_Entidad");
        builder.HasIndex(x => x.AtUtc).HasDatabaseName("IX_AuditLog_Fecha");
    }
}

/// <summary>Arma filas de auditoría con Antes/Despues en JSON legible (sin escapar tildes).</summary>
public static class AuditEntry
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AuditLog Create(string user, string action, string entity, string? entityId, object? before, object? after,
        DateTime atUtc) => new()
        {
            User = user,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Before = before is null ? null : System.Text.Json.JsonSerializer.Serialize(before, Json),
            After = after is null ? null : System.Text.Json.JsonSerializer.Serialize(after, Json),
            AtUtc = atUtc,
        };
}
