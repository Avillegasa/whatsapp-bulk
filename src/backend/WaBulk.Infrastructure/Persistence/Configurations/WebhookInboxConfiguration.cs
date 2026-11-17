using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Persistence.Configurations;

internal sealed class WebhookInboxConfiguration : IEntityTypeConfiguration<WebhookInbox>
{
    public void Configure(EntityTypeBuilder<WebhookInbox> builder)
    {
        builder.ToTable("WebhookInbox");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ReceivedUtc).HasColumnName("RecibidoUtc");
        builder.Property(x => x.Body).HasColumnName("Cuerpo");
        builder.Property(x => x.Status).HasColumnName("Estado")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Attempts).HasColumnName("Intentos");
        builder.Property(x => x.NextAttemptUtc).HasColumnName("ProximoIntentoUtc");

        // Lectura del procesador: Pendiente, o EnEspera con ProximoIntentoUtc vencido.
        builder.HasIndex(x => new { x.Status, x.NextAttemptUtc });
    }
}
