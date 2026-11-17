using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Campaigns;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Contacts;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Tests.Support;

/// <summary>Arma los servicios como en producción, sobre el contexto de la prueba.</summary>
public static class TestServices
{
    public static WebhookInboxProcessor Processor(
        WaBulkDbContext db, TimeProvider clock, InboxProcessorOptions? options = null, OptOutOptions? optOut = null) =>
        new(db, Options.Create(options ?? new InboxProcessorOptions()), Options.Create(optOut ?? new OptOutOptions()),
            new AlertService(db, clock, NullLogger<AlertService>.Instance),
            new ContactDirectory(db, clock, NullLogger<ContactDirectory>.Instance), clock, NullLogger<WebhookInboxProcessor>.Instance);

    public static CampaignLifecycle Lifecycle(WaBulkDbContext db) => new(db, Options.Create(new CampaignOptions()));

    /// <summary>
    /// Versión de campaña lista para enviar (F1.6: DispatchJob.CampaignVersionId tiene FK): plantilla APPROVED de
    /// marketing sin parámetros (se crea si no existe), campaña Scheduled y una versión nueva por llamada, programada en
    /// el pasado y sin vencer (cada trabajo tiene su propio ExpiraUtc).
    /// </summary>
    public static async Task<long> CampaignVersionAsync(
        WaBulkDbContext db, string templateName = "promo_prueba", string language = "es",
        CampaignStatus status = CampaignStatus.Scheduled)
    {
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Name == templateName && t.Language == language);
        if (template is null)
        {
            template = new Template
            {
                MetaTemplateId = $"{Random.Shared.NextInt64(1, long.MaxValue)}", Name = templateName, Language = language,
                Category = TemplateCategories.Marketing, Status = TemplateStatuses.Approved, ParameterFormat = "NAMED",
                Components = """[{"type":"BODY","text":"Promo de prueba"}]""", CreatedUtc = DateTime.UtcNow, CreatedBy = "prueba",
            };
            db.Templates.Add(template);
            await db.SaveChangesAsync();
        }

        var campaign = new Campaign { Name = "Prueba", Status = status, StatusUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow, CreatedBy = "prueba" };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();
        var version = new CampaignVersion
        {
            CampaignId = campaign.Id, Number = 1, TemplateId = template.Id, TemplateName = template.Name, Language = template.Language,
            Category = template.Category, ParameterFormat = "NAMED", Audience = "[]", Parameters = "[]",
            ScheduledUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), ExpiresUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Hash = "prueba", CreatedUtc = DateTime.UtcNow, CreatedBy = "prueba",
        };
        db.CampaignVersions.Add(version);
        await db.SaveChangesAsync();
        campaign.CurrentVersionId = version.Id;
        await db.SaveChangesAsync();
        return version.Id;
    }

    /// <summary>Id del Contact del teléfono (lo crea si no existe): DispatchJob.ContactId tiene FK desde F1.4.</summary>
    public static async Task<long> ContactIdAsync(WaBulkDbContext db, string phoneE164)
    {
        var existing = await db.Contacts.Where(c => c.PhoneE164 == phoneE164).Select(c => (long?)c.Id).FirstOrDefaultAsync();
        if (existing is { } id)
        {
            return id;
        }

        var contact = new Contact { PhoneE164 = phoneE164, WaId = phoneE164.TrimStart('+'), CreatedUtc = DateTime.UtcNow, CreatedBy = "prueba" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return contact.Id;
    }
}
