using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Alerts;

namespace WaBulk.Infrastructure.Persistence.Configurations;

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.ToTable("Alert");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Type).HasColumnName("Tipo").HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.Key).HasColumnName("Clave").HasMaxLength(200);
        builder.Property(x => x.Message).HasColumnName("Mensaje").HasMaxLength(500);
        builder.Property(x => x.Detail).HasColumnName("Detalle");
        builder.Property(x => x.Source).HasColumnName("Origen").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.Occurrences).HasColumnName("Ocurrencias");
        builder.Property(x => x.CreatedUtc).HasColumnName("CreadaUtc");
        builder.Property(x => x.LastUtc).HasColumnName("UltimaUtc");
        builder.Property(x => x.ResolvedUtc).HasColumnName("ResueltaUtc");
        builder.Property(x => x.ResolvedBy).HasColumnName("ResueltaPor").HasMaxLength(100);

        // Una sola alerta abierta por tipo y clave (regla 11: filtrado).
        builder.HasIndex(x => new { x.Type, x.Key }).IsUnique().HasFilter("[ResueltaUtc] IS NULL")
            .HasDatabaseName("IX_Alert_Abierta");
    }
}

internal sealed class DispatchPauseConfiguration : IEntityTypeConfiguration<DispatchPause>
{
    public void Configure(EntityTypeBuilder<DispatchPause> builder)
    {
        builder.ToTable("DispatchPause");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Scope).HasColumnName("Alcance").HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Reason).HasColumnName("Motivo").HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.FromUtc).HasColumnName("DesdeUtc");
        builder.Property(x => x.LiftedUtc).HasColumnName("LevantadaUtc");
        builder.Property(x => x.LiftedBy).HasColumnName("LevantadaPor").HasMaxLength(100);

        // Una sola pausa activa por alcance (regla 11: filtrado).
        builder.HasIndex(x => x.Scope).IsUnique().HasFilter("[LevantadaUtc] IS NULL").HasDatabaseName("IX_DispatchPause_Activa");
        builder.HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict);
    }
}
