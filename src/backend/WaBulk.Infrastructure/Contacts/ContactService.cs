using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Contacts;

/// <summary>Estado de consentimiento que muestra la intranet (capa empresa; null = nunca dio consentimiento).</summary>
public static class ContactConsentStates
{
    public const string SinConsentimiento = "SinConsentimiento";

    public static string From(ConsentStatus? status) => status?.ToString() ?? SinConsentimiento;
}

public sealed record ContactSummary(
    long Id, string? PhoneE164, string? Name, string? CustomerId, bool IsActive, string Consent, bool PlatformStop,
    DateTime? LastInboundUtc);

public sealed record ConsentHistoryItem(DateTime RecordedUtc, string Status, string Source, string? Evidence, string RecordedBy);

public sealed record InboundItem(long Id, DateTime SentUtc, string Type, string? Text, string? ButtonId);

public sealed record OfferItem(long Id, DateTime OfferedUtc, string OfferedBy, string TextVersion, string Status, string? Response, DateTime? ResponseUtc);

public sealed record ContactDetail(
    ContactSummary Contact, string? UserId, IReadOnlyList<ConsentHistoryItem> History, IReadOnlyList<InboundItem> Messages,
    IReadOnlyList<OfferItem> Offers, IReadOnlyList<string> Segments);

public sealed record ContactActionResult(bool Succeeded, string? Error, string? Message)
{
    public static readonly ContactActionResult Ok = new(true, null, null);
    public static ContactActionResult NotFound => new(false, "NoExiste", "El contacto no existe.");
    public static ContactActionResult Fail(string error, string message) => new(false, error, message);
}

/// <summary>Contactos para la intranet (F1.4): lista con su estado, detalle y baja manual auditada.</summary>
public sealed class ContactService(WaBulkDbContext db, TimeProvider clock)
{
    public const int MaxListed = 500;

    /// <param name="consent">OptIn, OptOut, NoContactar, SinConsentimiento o null (todos).</param>
    /// <param name="search">Parte del teléfono, del nombre o del código de cliente.</param>
    /// <param name="segmentId">Solo los miembros de esa lista.</param>
    public async Task<IReadOnlyList<ContactSummary>> ListAsync(
        string? consent, string? search, CancellationToken cancellationToken, long? segmentId = null)
    {
        var contacts = db.Contacts.AsNoTracking();
        if (segmentId is { } sid)
        {
            contacts = contacts.Where(c => db.SegmentMembers.Any(m => m.SegmentId == sid && m.ContactId == c.Id));
        }

        var query = Summaries(contacts);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(c => (c.PhoneE164 != null && c.PhoneE164.Contains(s)) || (c.Name != null && c.Name.Contains(s))
                                     || (c.CustomerId != null && c.CustomerId.Contains(s)));
        }

        if (!string.IsNullOrWhiteSpace(consent))
        {
            query = consent == ContactConsentStates.SinConsentimiento
                ? query.Where(c => c.Consent == null)
                : Enum.TryParse<ConsentStatus>(consent, out var status) ? query.Where(c => c.Consent == status) : query.Where(_ => false);
        }

        var rows = await query.OrderByDescending(c => c.LastInboundUtc).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Take(MaxListed).ToListAsync(cancellationToken);
        return [.. rows.Select(ToSummary)];
    }

    public async Task<ContactDetail?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var row = await Summaries(db.Contacts.AsNoTracking().Where(c => c.Id == id)).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var history = row.PhoneE164 is null ? [] : await db.ConsentRecords.AsNoTracking()
            .Where(r => r.PhoneE164 == row.PhoneE164 && r.Purpose == ConsentPurposes.Promociones)
            .OrderByDescending(r => r.RecordedUtc).ThenByDescending(r => r.Id)
            .Select(r => new ConsentHistoryItem(r.RecordedUtc, r.Status.ToString(), r.Source.ToString(), r.Evidence, r.RecordedBy))
            .ToListAsync(cancellationToken);
        var messages = await db.InboundMessages.AsNoTracking().Where(m => m.ContactId == id)
            .OrderByDescending(m => m.SentUtc).Take(20)
            .Select(m => new InboundItem(m.Id, m.SentUtc, m.Type, m.Text, m.ButtonId)).ToListAsync(cancellationToken);
        var offers = await db.ConsentOffers.AsNoTracking().Where(o => o.ContactId == id)
            .OrderByDescending(o => o.OfferedUtc).Take(20)
            .Select(o => new OfferItem(o.Id, o.OfferedUtc, o.OfferedBy, o.TextVersion, o.Status.ToString(),
                o.Response == null ? null : o.Response.ToString(), o.ResponseUtc))
            .ToListAsync(cancellationToken);
        var segments = await (from m in db.SegmentMembers.AsNoTracking()
                              join s in db.Segments.AsNoTracking() on m.SegmentId equals s.Id
                              where m.ContactId == id
                              orderby s.Name
                              select s.Name).ToListAsync(cancellationToken);
        var userId = await db.Contacts.AsNoTracking().Where(c => c.Id == id).Select(c => c.UserId).SingleAsync(cancellationToken);
        return new ContactDetail(ToSummary(row), userId, history, messages, offers, segments);
    }

    /// <summary>
    /// Baja pedida fuera de WhatsApp (por teléfono, en la tienda…): ConsentRecord OptOut con Fuente Intranet y su
    /// AuditLog en la misma transacción. El motivo es obligatorio: es la evidencia.
    /// </summary>
    public async Task<ContactActionResult> RecordOptOutAsync(string actor, long contactId, string? reason, CancellationToken cancellationToken)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return ContactActionResult.Fail("SinMotivo", "Escribe el motivo o cómo pidió la baja (queda como evidencia).");
        }

        if (text.Length > 500)
        {
            return ContactActionResult.Fail("MotivoLargo", "El motivo admite hasta 500 caracteres.");
        }

        var contact = await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contactId, cancellationToken);
        if (contact is null)
        {
            return ContactActionResult.NotFound;
        }

        if (contact.PhoneE164 is null)
        {
            return ContactActionResult.Fail("SinTelefono", "El contacto no tiene teléfono: la baja se registra por teléfono.");
        }

        var current = await db.ConsentRecords.AsNoTracking()
            .Where(r => r.PhoneE164 == contact.PhoneE164 && r.Purpose == ConsentPurposes.Promociones)
            .OrderByDescending(r => r.RecordedUtc).ThenByDescending(r => r.Id)
            .Select(r => (ConsentStatus?)r.Status).FirstOrDefaultAsync(cancellationToken);
        if (current is ConsentStatus.OptOut or ConsentStatus.NoContactar)
        {
            return ContactActionResult.Fail("YaTieneBaja", "El contacto ya tiene la baja registrada.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ConsentRecords.Add(new ConsentRecord
        {
            ContactId = contact.Id,
            PhoneE164 = contact.PhoneE164,
            Status = ConsentStatus.OptOut,
            Source = ConsentSource.Intranet,
            Evidence = text,
            RecordedUtc = now,
            RecordedBy = actor,
        });
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.BajaManual, "Contacto", contact.Id.ToString(CultureInfo.InvariantCulture),
            new { telefono = contact.PhoneE164, consentimiento = ContactConsentStates.From(current) },
            new { telefono = contact.PhoneE164, consentimiento = nameof(ConsentStatus.OptOut), motivo = text }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ContactActionResult.Ok;
    }

    // Clase con propiedades init (no constructor): EF puede seguir filtrando y ordenando después del Select.
    private sealed class SummaryRow
    {
        public long Id { get; init; }
        public string? PhoneE164 { get; init; }
        public string? Name { get; init; }
        public string? CustomerId { get; init; }
        public bool IsActive { get; init; }
        public ConsentStatus? Consent { get; init; }
        public PlatformPreferenceValue? Platform { get; init; }
        public DateTime? LastInboundUtc { get; init; }
    }

    /// <summary>Contacto + consentimiento vigente + preferencia de plataforma vigente + último mensaje (una sola consulta).</summary>
    private IQueryable<SummaryRow> Summaries(IQueryable<Contact> contacts) =>
        from c in contacts
        select new SummaryRow
        {
            Id = c.Id,
            PhoneE164 = c.PhoneE164,
            Name = c.Name,
            CustomerId = c.CustomerId,
            IsActive = c.IsActive,
            Consent = db.ConsentRecords.Where(r => r.PhoneE164 == c.PhoneE164 && r.Purpose == ConsentPurposes.Promociones)
                .OrderByDescending(r => r.RecordedUtc).ThenByDescending(r => r.Id).Select(r => (ConsentStatus?)r.Status).FirstOrDefault(),
            Platform = db.PlatformPreferences.Where(p => p.PhoneE164 == c.PhoneE164 && p.Category == PlatformCategories.MarketingMessages)
                .OrderByDescending(p => p.RecordedUtc).ThenByDescending(p => p.Id).Select(p => (PlatformPreferenceValue?)p.Value).FirstOrDefault(),
            LastInboundUtc = db.InboundMessages.Where(m => m.ContactId == c.Id).Max(m => (DateTime?)m.SentUtc),
        };

    private static ContactSummary ToSummary(SummaryRow r) => new(
        r.Id, r.PhoneE164, r.Name, r.CustomerId, r.IsActive, ContactConsentStates.From(r.Consent),
        r.Platform == PlatformPreferenceValue.Stop, r.LastInboundUtc);
}
