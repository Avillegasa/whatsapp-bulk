using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Capacity;
using WaBulk.Infrastructure.Campaigns;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Contacts;
using WaBulk.Infrastructure.Meta;

namespace WaBulk.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Registra el procesador de WebhookInbox con la sección "InboxProcessor".</summary>
    public static IServiceCollection AddWebhookInboxProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InboxProcessorOptions>()
            .Bind(configuration.GetSection(InboxProcessorOptions.SectionName))
            .Validate(o => o.BatchSize > 0 && o.PollSeconds > 0 && o.MaxAttempts > 0
                    && o.RetryDelaySeconds > 0 && o.MaxRetryDelaySeconds >= o.RetryDelaySeconds,
                "InboxProcessor: valores fuera de rango")
            .ValidateOnStart();
        services.AddScoped<WebhookInboxProcessor>();
        services.TryAddScoped<ContactDirectory>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddConsent(configuration);
        services.TryAddScoped<AlertService>();
        return services;
    }

    /// <summary>Bajas (F0.10): sección "Bajas", ConsentService y EnqueueService. Se puede llamar más de una vez.</summary>
    public static IServiceCollection AddConsent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OptOutOptions>().Bind(configuration.GetSection(OptOutOptions.SectionName));
        services.TryAddScoped<ConsentService>();
        services.TryAddScoped<EnqueueService>();
        return services;
    }

    /// <summary>Registra el despachador (F0.9) con la sección "Dispatcher". Requiere AddMetaWhatsApp.</summary>
    public static IServiceCollection AddDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DispatcherOptions>()
            .Bind(configuration.GetSection(DispatcherOptions.SectionName))
            .Validate(o => o.BatchSize > 0 && o.PollSeconds > 0 && o.RetryBaseSeconds > 0 && o.RetryMaxSeconds >= o.RetryBaseSeconds,
                "Dispatcher: valores fuera de rango")
            .Validate(o => o.MaxAttempts is >= 1 and <= 5, "Dispatcher:MaxAttempts debe estar entre 1 y 5 (regla 9)")
            .Validate<IOptions<MetaOptions>>(
                (o, meta) => o.LeaseSeconds >= 2 * meta.Value.RequestTimeoutSeconds,
                "Dispatcher:LeaseSeconds debe ser al menos el doble de Meta:RequestTimeoutSeconds (el lease no puede vencer durante un POST)")
            .Validate(o => o.MessagesPerSecond is >= 1 and <= 80 && o.RateBurst is >= 1 and <= 80,
                "Dispatcher:MessagesPerSecond y RateBurst deben estar entre 1 y 80 (techo de Meta por número)")
            .Validate(o => o.FatigueStreak >= 1 && o.FatiguePauseDays >= 1 && o.FatigueConsecutive131049 >= 1,
                "Dispatcher: valores de fatiga fuera de rango")
            .Validate(o => o.FailureWindow >= 1 && o.FailureMinSample >= 1 && o.FailureThresholdPercent is > 0 and < 100,
                "Dispatcher: valores del freno por tasa de fallos fuera de rango")
            .Validate(o => TryWindow(o), "Dispatcher:AllowedFrom/AllowedUntil (HH:mm) o TimeZone (IANA, ej. America/La_Paz) inválidos")
            .ValidateOnStart();
        services.AddOptions<FrequencyOptions>()
            .Bind(configuration.GetSection(FrequencyOptions.SectionName))
            .Validate(o => o.MaxPromotionsPerWeek >= 1, "OfertaPromociones:MaxPromotionsPerWeek debe ser al menos 1")
            .ValidateOnStart();
        services.AddSingleton<SendRateLimiter>();
        services.AddSingleton<DispatcherIdentity>();
        services.AddScoped<DispatchQueue>();
        services.AddScoped<Dispatcher>();
        services.AddConsent(configuration);
        services.AddOptions<CapacityOptions>()
            .Bind(configuration.GetSection(CapacityOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.PortfolioId), "Capacity:PortfolioId es obligatorio")
            .Validate(o => o.PortfolioLimit > 0 && o.UsagePercent is > 0 and <= 100 && o.ManualQuota >= 0 && o.LockTimeoutMs > 0,
                "Capacity: valores fuera de rango")
            .ValidateOnStart();
        services.AddScoped<CapacityService>();
        services.TryAddScoped<AlertService>();
        services.AddCampaigns(configuration); // F1.6: ciclo de vida de las campañas en cada vuelta.
        return services;
    }

    private static bool TryWindow(DispatcherOptions o)
    {
        try
        {
            _ = o.Window;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
