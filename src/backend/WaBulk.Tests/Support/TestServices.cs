using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Contacts;
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
