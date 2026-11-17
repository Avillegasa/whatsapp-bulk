using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WaBulk.Infrastructure.Campaigns;

public static class CampaignsServiceCollectionExtensions
{
    /// <summary>Campañas (F1.6), sección "Campanias". Requiere AddConsent (bajas revisadas al aprobar).</summary>
    public static IServiceCollection AddCampaigns(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CampaignOptions>()
            .Bind(configuration.GetSection(CampaignOptions.SectionName))
            .Validate(o => o.TrackingHours > 0, "Campanias:TrackingHours debe ser mayor que 0.");
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<CampaignService>();
        services.TryAddScoped<CampaignLifecycle>();
        return services;
    }
}
