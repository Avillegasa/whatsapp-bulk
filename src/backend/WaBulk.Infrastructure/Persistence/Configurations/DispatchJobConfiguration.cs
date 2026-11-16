using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Persistence.Configurations;

internal sealed class DispatchJobConfiguration : IEntityTypeConfiguration<DispatchJob>
{
    public void Configure(EntityTypeBuilder<DispatchJob> builder)
    {
        builder.ToTable("DispatchJob");
        builder.HasKey(x => x.Id);

        // CampaignVersionId y ContactId quedan sin FK hasta que existan CampaignVersion y Contact (Fase 1).
        builder.Property(x => x.PhoneE164).HasMaxLength(16).IsUnicode(false);
        builder.Property(x => x.Parameters).HasColumnName("Parametros");
        builder.Property(x => x.Priority).HasColumnName("Prioridad");
        builder.Property(x => x.ScheduledUtc).HasColumnName("ProgramadoUtc");
        builder.Property(x => x.ExpiresUtc).HasColumnName("ExpiraUtc");
        builder.Property(x => x.DispatchStatus).HasColumnName("EstadoDespacho")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Attempts).HasColumnName("Intentos");
        builder.Property(x => x.NextAttemptUtc).HasColumnName("ProximoIntentoUtc");
        builder.Property(x => x.LockedBy).HasMaxLength(100);

        // Un destinatario por versión de campaña.
        builder.HasIndex(x => new { x.CampaignVersionId, x.ContactId }).IsUnique();
    }
}
