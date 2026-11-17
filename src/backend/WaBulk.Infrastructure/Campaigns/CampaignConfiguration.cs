using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Templates;

namespace WaBulk.Infrastructure.Campaigns;

internal sealed class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        builder.ToTable("Campaign");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasColumnName("Nombre").HasMaxLength(200);
        builder.Property(x => x.Status).HasColumnName("Estado").HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.CurrentVersionId).HasColumnName("VersionActualId");
        builder.Property(x => x.StatusReason).HasColumnName("MotivoEstado").HasMaxLength(1000);
        builder.Property(x => x.StatusUtc).HasColumnName("EstadoUtc");
        builder.Property(x => x.CompletedUtc).HasColumnName("DespachoCompletoUtc");
        builder.Property(x => x.CreatedUtc).HasColumnName("CreadaUtc");
        builder.Property(x => x.CreatedBy).HasColumnName("CreadaPor").HasMaxLength(256);

        // Sin FK a CampaignVersion: la versión apunta a la campaña (evita el ciclo de FKs al insertar).
        builder.HasIndex(x => x.Status);
    }
}

internal sealed class CampaignVersionConfiguration : IEntityTypeConfiguration<CampaignVersion>
{
    public void Configure(EntityTypeBuilder<CampaignVersion> builder)
    {
        builder.ToTable("CampaignVersion");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Number).HasColumnName("Numero");
        builder.Property(x => x.TemplateName).HasColumnName("NombrePlantilla").HasMaxLength(512).IsUnicode(false);
        builder.Property(x => x.Language).HasColumnName("Idioma").HasMaxLength(15).IsUnicode(false);
        builder.Property(x => x.Category).HasColumnName("Categoria").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.ParameterFormat).HasColumnName("FormatoParametros").HasMaxLength(15).IsUnicode(false);
        builder.Property(x => x.Audience).HasColumnName("Audiencia");
        builder.Property(x => x.AudienceCount).HasColumnName("Destinatarios");
        builder.Property(x => x.ExcludedCount).HasColumnName("Excluidos");
        builder.Property(x => x.Parameters).HasColumnName("Parametros");
        builder.Property(x => x.ScheduledUtc).HasColumnName("ProgramadaUtc");
        builder.Property(x => x.ExpiresUtc).HasColumnName("ExpiraUtc");
        builder.Property(x => x.Hash).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.CreatedUtc).HasColumnName("CreadaUtc");
        builder.Property(x => x.CreatedBy).HasColumnName("CreadaPor").HasMaxLength(256);
        builder.Property(x => x.SubmittedUtc).HasColumnName("EnviadaAprobacionUtc");
        builder.Property(x => x.ApprovedUtc).HasColumnName("AprobadaUtc");
        builder.Property(x => x.ApprovedBy).HasColumnName("AprobadaPor").HasMaxLength(256);
        builder.Property(x => x.RejectedUtc).HasColumnName("RechazadaUtc");
        builder.Property(x => x.RejectedBy).HasColumnName("RechazadaPor").HasMaxLength(256);
        builder.Property(x => x.RejectionReason).HasColumnName("MotivoRechazo").HasMaxLength(1000);

        builder.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Template>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Segment>().WithMany().HasForeignKey(x => x.SegmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CampaignId, x.Number }).IsUnique();
    }
}
