using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Templates;

namespace WaBulk.Infrastructure.Templates;

internal sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.ToTable("Template");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MetaTemplateId).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.Name).HasColumnName("Nombre").HasMaxLength(512).IsUnicode(false);
        builder.Property(x => x.Language).HasColumnName("Idioma").HasMaxLength(15).IsUnicode(false);
        builder.Property(x => x.Category).HasColumnName("Categoria").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.Status).HasColumnName("Estado").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.StatusUtc).HasColumnName("EstadoUtc");
        builder.Property(x => x.StatusReason).HasColumnName("MotivoEstado").HasMaxLength(4000);
        builder.Property(x => x.LastEvent).HasColumnName("UltimoEvento").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.Quality).HasColumnName("Calidad").HasMaxLength(15).IsUnicode(false);
        builder.Property(x => x.QualityUtc).HasColumnName("CalidadUtc");
        builder.Property(x => x.PreviousCategory).HasColumnName("CategoriaAnterior").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.CorrectCategory).HasColumnName("CategoriaNueva").HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.CategoryChangeUtc).HasColumnName("CambioCategoriaUtc");
        builder.Property(x => x.ParameterFormat).HasColumnName("FormatoParametros").HasMaxLength(15).IsUnicode(false);
        builder.Property(x => x.Components).HasColumnName("Componentes");
        builder.Property(x => x.SyncedUtc).HasColumnName("SincronizadaUtc");
        builder.Property(x => x.CreatedUtc).HasColumnName("CreadaUtc");
        builder.Property(x => x.CreatedBy).HasColumnName("CreadaPor").HasMaxLength(256);

        // Meta exige nombre único por idioma; aquí también.
        builder.HasIndex(x => new { x.Name, x.Language }).IsUnique();
        // Regla 11: MetaTemplateId es NULL mientras se crea o si la creación quedó incierta.
        builder.HasIndex(x => x.MetaTemplateId).IsUnique().HasFilter("[MetaTemplateId] IS NOT NULL");
    }
}
