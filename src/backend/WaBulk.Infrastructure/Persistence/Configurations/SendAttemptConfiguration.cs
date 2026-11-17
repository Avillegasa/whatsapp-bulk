using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Persistence.Configurations;

internal sealed class SendAttemptConfiguration : IEntityTypeConfiguration<SendAttempt>
{
    public void Configure(EntityTypeBuilder<SendAttempt> builder)
    {
        builder.ToTable("SendAttempt");
        builder.HasKey(x => x.AttemptId);
        // El AttemptId lo genera la aplicación antes del POST (viaja en biz_opaque_callback_data).
        builder.Property(x => x.AttemptId).ValueGeneratedNever();
        builder.Property(x => x.StartedUtc).HasColumnName("InicioUtc");
        builder.Property(x => x.FinishedUtc).HasColumnName("FinUtc");
        builder.Property(x => x.Status).HasColumnName("Estado")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Wamid).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.ErrorCode).HasColumnName("CodigoError");
        builder.Property(x => x.Detail).HasColumnName("Detalle");
        builder.Property(x => x.DeliveryStatus).HasColumnName("EstadoEntrega")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);

        builder.HasOne(x => x.DispatchJob)
            .WithMany(j => j.SendAttempts)
            .HasForeignKey(x => x.DispatchJobId)
            .OnDelete(DeleteBehavior.Restrict);

        // Regla 11: índice único sobre columna que admite NULL, siempre filtrado
        // (puede haber muchos intentos sin wamid: rechazados o SubmissionUnknown).
        builder.HasIndex(x => x.Wamid).IsUnique().HasFilter("[Wamid] IS NOT NULL");
    }
}
