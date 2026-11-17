using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        return services;
    }
}
