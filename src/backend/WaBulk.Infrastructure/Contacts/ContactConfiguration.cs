using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Contacts;

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("Contact");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PhoneE164).HasMaxLength(16).IsUnicode(false);
        builder.Property(x => x.WaId).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.UserId).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.Name).HasColumnName("Nombre").HasMaxLength(200);
        builder.Property(x => x.CustomerId).HasColumnName("ClienteId").HasMaxLength(50);
        builder.Property(x => x.IsActive).HasColumnName("Activo").HasDefaultValue(true);
        builder.Property(x => x.CreatedUtc).HasColumnName("CreadoUtc");
        builder.Property(x => x.CreatedBy).HasColumnName("CreadoPor").HasMaxLength(256);

        // Regla 11: los dos identificadores admiten NULL (BSUID sin teléfono, contacto importado sin BSUID).
        builder.HasIndex(x => x.PhoneE164).IsUnique().HasFilter("[PhoneE164] IS NOT NULL");
        builder.HasIndex(x => x.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL");
    }
}

internal sealed class InboundMessageConfiguration : IEntityTypeConfiguration<InboundMessage>
{
    public void Configure(EntityTypeBuilder<InboundMessage> builder)
    {
        builder.ToTable("InboundMessage");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Wamid).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.PhoneE164).HasMaxLength(16).IsUnicode(false);
        builder.Property(x => x.UserId).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.Type).HasColumnName("Tipo").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.Text).HasColumnName("Texto").HasMaxLength(4000);
        builder.Property(x => x.ButtonId).HasColumnName("BotonId").HasMaxLength(256);
        builder.Property(x => x.ContextWamid).HasColumnName("WamidContexto").HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.SentUtc).HasColumnName("FechaUtc");
        builder.Property(x => x.ReceivedUtc).HasColumnName("RecibidoUtc");

        builder.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WebhookInbox>().WithMany().HasForeignKey(x => x.WebhookInboxId).OnDelete(DeleteBehavior.Restrict);

        // Idempotencia: un webhook repetido no duplica el mensaje.
        builder.HasIndex(x => x.Wamid).IsUnique();
        // Ventana de 24 h y "Mensajes recientes": último mensaje por contacto.
        builder.HasIndex(x => new { x.ContactId, x.SentUtc });
        builder.HasIndex(x => x.SentUtc);
    }
}

internal sealed class ConsentOfferConfiguration : IEntityTypeConfiguration<ConsentOffer>
{
    public void Configure(EntityTypeBuilder<ConsentOffer> builder)
    {
        builder.ToTable("ConsentOffer");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PhoneE164).HasMaxLength(16).IsUnicode(false);
        builder.Property(x => x.TextVersion).HasColumnName("VersionTexto").HasMaxLength(40);
        builder.Property(x => x.Text).HasColumnName("Texto").HasMaxLength(1024);
        builder.Property(x => x.OfferedBy).HasColumnName("OfrecidoPor").HasMaxLength(256);
        builder.Property(x => x.OfferedUtc).HasColumnName("FechaUtc");
        builder.Property(x => x.Status).HasColumnName("Estado")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Wamid).HasMaxLength(128).IsUnicode(false);
        builder.Property(x => x.ErrorCode).HasColumnName("CodigoError");
        builder.Property(x => x.Detail).HasColumnName("Detalle").HasMaxLength(4000);
        builder.Property(x => x.Response).HasColumnName("Respuesta")
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.ResponseUtc).HasColumnName("RespuestaUtc");
        builder.Property(x => x.ResponseWamid).HasColumnName("WamidRespuesta").HasMaxLength(128).IsUnicode(false);

        builder.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);

        // La respuesta trae context.id = wamid de la oferta (regla 11: filtrado).
        builder.HasIndex(x => x.Wamid).IsUnique().HasFilter("[Wamid] IS NOT NULL");
        builder.HasIndex(x => new { x.ContactId, x.OfferedUtc });
    }
}

internal sealed class SegmentConfiguration : IEntityTypeConfiguration<Segment>
{
    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        builder.ToTable("Segment");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasColumnName("Nombre").HasMaxLength(100);
        builder.Property(x => x.Description).HasColumnName("Descripcion").HasMaxLength(500);
        builder.Property(x => x.CreatedUtc).HasColumnName("CreadoUtc");
        builder.Property(x => x.CreatedBy).HasColumnName("CreadoPor").HasMaxLength(256);
        builder.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class SegmentMemberConfiguration : IEntityTypeConfiguration<SegmentMember>
{
    public void Configure(EntityTypeBuilder<SegmentMember> builder)
    {
        builder.ToTable("SegmentMember");
        builder.HasKey(x => new { x.SegmentId, x.ContactId });
        builder.Property(x => x.AddedUtc).HasColumnName("AgregadoUtc");
        builder.Property(x => x.AddedBy).HasColumnName("AgregadoPor").HasMaxLength(256);
        builder.HasOne<Segment>().WithMany().HasForeignKey(x => x.SegmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ContactId);
    }
}

/// <summary>FK hacia Contact de tablas anteriores a la Fase 1 (pendiente de F0.4/F0.10).</summary>
internal sealed class ContactForeignKeys : IEntityTypeConfiguration<ConsentRecord>, IEntityTypeConfiguration<DispatchJob>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder) =>
        builder.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);

    public void Configure(EntityTypeBuilder<DispatchJob> builder) =>
        builder.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
}
