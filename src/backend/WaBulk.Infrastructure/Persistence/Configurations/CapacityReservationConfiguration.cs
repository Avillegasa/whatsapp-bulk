using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Capacity;

namespace WaBulk.Infrastructure.Persistence.Configurations;

internal sealed class CapacityReservationConfiguration : IEntityTypeConfiguration<CapacityReservation>
{
    public void Configure(EntityTypeBuilder<CapacityReservation> builder)
    {
        builder.ToTable("CapacityReservation");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PortfolioId).HasColumnName("PortafolioId").HasMaxLength(32).IsUnicode(false);
        builder.Property(x => x.PhoneE164).HasMaxLength(16).IsUnicode(false);
        builder.Property(x => x.ReservedUtc).HasColumnName("ReservadaUtc");
        builder.Property(x => x.ExpiresUtc).HasColumnName("ExpiraUtc");
        builder.Property(x => x.Status).HasColumnName("Estado")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);

        // Conteo de destinatarios únicos vigentes del portafolio (la ventana móvil de 24 h).
        builder.HasIndex(x => new { x.PortfolioId, x.ExpiresUtc })
            .IncludeProperties(x => new { x.PhoneE164, x.Status })
            .HasDatabaseName("IX_CapacityReservation_Ventana");
        // Consumir o liberar la reserva de un trabajo.
        builder.HasIndex(x => x.DispatchJobId);
    }
}
