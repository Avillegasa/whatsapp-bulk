using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WaBulk.Infrastructure.Meta;

public static class MetaServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="MetaWhatsAppClient"/>. Deliberadamente SIN AddStandardResilienceHandler ni políticas
    /// de reintento: un reintento automático sobre /messages puede duplicar envíos (regla 3, D-010).
    /// </summary>
    public static IServiceCollection AddMetaWhatsApp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MetaOptions>()
            .Bind(configuration.GetSection(MetaOptions.SectionName))
            .Validate(o => o.RequestTimeoutSeconds is > 0 and <= 120, "Meta:RequestTimeoutSeconds debe estar entre 1 y 120.");

        services.AddHttpClient<MetaWhatsAppClient>((sp, client) =>
        {
            var meta = sp.GetRequiredService<IOptions<MetaOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(meta.RequestTimeoutSeconds);
        });

        // F1.5: API de plantillas de la WABA (listar y crear), también sin reintentos.
        services.AddHttpClient<MetaTemplateApiClient>((sp, client) =>
        {
            var meta = sp.GetRequiredService<IOptions<MetaOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(meta.RequestTimeoutSeconds);
        });

        return services;
    }
}
