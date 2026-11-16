using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Persistence.Configurations;

internal sealed class MessageEventConfiguration : IEntityTypeConfiguration<MessageEvent>
{
    public void Configure(EntityTypeBuilder<MessageEvent> builder)
    {
        builder.ToTable("MessageEvent");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Wamid).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.Status).HasColumnName("Estado")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.MetaTimestampUtc).HasColumnName("TimestampMeta");
        builder.Property(x => x.Errors).HasColumnName("Errores");
        builder.Property(x => x.CreatedUtc).HasColumnName("CreadoUtc");

        // Idempotencia: el mismo estado de un wamid se registra una sola vez.
        builder.HasIndex(x => new { x.Wamid, x.Status }).IsUnique();
        // Correlación por AttemptId cuando el webhook llega antes de guardar el wamid (F0.7).
        builder.HasIndex(x => x.AttemptId);
    }
}
