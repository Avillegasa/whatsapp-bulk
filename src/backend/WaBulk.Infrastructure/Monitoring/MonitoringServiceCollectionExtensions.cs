using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Capacity;

namespace WaBulk.Infrastructure.Monitoring;

public static class MonitoringServiceCollectionExtensions
{
    /// <summary>
    /// Tablero (F1.8) para la Api: secciones "Monitor" y "Capacity" (los mismos valores que el Worker; una prueba exige que
    /// coincidan), CapacityService para el presupuesto y AlertService para reanudar.
    /// </summary>
    public static IServiceCollection AddMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MonitorOptions>()
            .Bind(configuration.GetSection(MonitorOptions.SectionName))
            .Validate(o => o.PushSeconds is >= 1 and <= 4 && o.NeedsReviewHours >= 1,
                "Monitor:PushSeconds debe estar entre 1 y 4 (el monitor debe actualizarse en menos de 5 s) y NeedsReviewHours ≥ 1")
            .ValidateOnStart();
        services.AddOptions<CapacityOptions>().Bind(configuration.GetSection(CapacityOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<CapacityService>();
        services.TryAddScoped<AlertService>();
        services.TryAddScoped<MonitorService>();
        return services;
    }
}
