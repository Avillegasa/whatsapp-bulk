using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WaBulk.Domain.Templates;

namespace WaBulk.Infrastructure.Templates;

public static class TemplatesServiceCollectionExtensions
{
    /// <summary>Plantillas (F1.5), sección "Plantillas". Requiere AddMetaWhatsApp (API de plantillas de la WABA)
    /// y AddConsent/AddContacts (sección "Bajas": el texto del botón de baja; sin ella, el valor por defecto).</summary>
    public static IServiceCollection AddTemplates(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TemplateOptions>()
            .Bind(configuration.GetSection(TemplateOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.InterestButton) && o.InterestButton.Length <= TemplateRules.ButtonMaxLength,
                $"Plantillas:InterestButton debe tener entre 1 y {TemplateRules.ButtonMaxLength} caracteres (límite de Meta).")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<TemplateService>();
        return services;
    }
}
