using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WaBulk.Infrastructure.Messaging;

namespace WaBulk.Infrastructure.Contacts;

public static class ContactsServiceCollectionExtensions
{
    /// <summary>
    /// Contactos, oferta de promociones (sección "OfertaPromociones", D-031), listas e importación (F1.4). La oferta
    /// envía por Meta: requiere AddMetaWhatsApp.
    /// </summary>
    public static IServiceCollection AddContacts(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PromotionOfferOptions>()
            .Bind(configuration.GetSection(PromotionOfferOptions.SectionName))
            .Validate(o => o.IsValid(out _), "OfertaPromociones: texto o botones fuera de los límites de Meta (título ≤ 20, cuerpo ≤ 1024)")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddConsent(configuration);
        services.TryAddScoped<ContactDirectory>();
        services.TryAddScoped<ConsentOfferService>();
        services.TryAddScoped<ContactService>();
        services.TryAddScoped<SegmentService>();
        services.TryAddScoped<ContactImportService>();
        return services;
    }
}
