using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Consent;

namespace WaBulk.Infrastructure.Persistence.Configurations;

internal sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.ToTable("ConsentRecord");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PhoneE164).HasMaxLength(16).IsUnicode(false);
        builder.Property(x => x.Purpose).HasColumnName("Finalidad").HasMaxLength(40).IsUnicode(false);
        builder.Property(x => x.Channel).HasColumnName("Canal").HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Status).HasColumnName("Estado")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Source).HasColumnName("Fuente")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Evidence).HasColumnName("Evidencia").HasMaxLength(1000);
        builder.Property(x => x.SourceWamid).HasColumnName("WamidOrigen").HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.RecordedUtc).HasColumnName("FechaUtc");
        builder.Property(x => x.RecordedBy).HasColumnName("RegistradoPor").HasMaxLength(100);

        // Estado vigente: último registro por teléfono y finalidad.
        builder.HasIndex(x => new { x.PhoneE164, x.Purpose, x.RecordedUtc });
        // Idempotencia: un mensaje entrante genera como máximo una baja (regla 11: filtrado).
        builder.HasIndex(x => x.SourceWamid).IsUnique().HasFilter("[WamidOrigen] IS NOT NULL");
    }
}

internal sealed class PlatformPreferenceConfiguration : IEntityTypeConfiguration<PlatformPreference>
{
    public void Configure(EntityTypeBuilder<PlatformPreference> builder)
    {
        builder.ToTable("PlatformPreference");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PhoneE164).HasMaxLength(16).IsUnicode(false);
        builder.Property(x => x.Category).HasColumnName("Categoria").HasMaxLength(40).IsUnicode(false);
        builder.Property(x => x.Value).HasColumnName("Valor")
            .HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        builder.Property(x => x.Origin).HasColumnName("Origen")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.RecordedUtc).HasColumnName("FechaUtc");

        // Idempotencia del webhook (el mismo user_preferences repetido) y lectura del vigente.
        builder.HasIndex(x => new { x.PhoneE164, x.Category, x.RecordedUtc, x.Value }).IsUnique();
    }
}
