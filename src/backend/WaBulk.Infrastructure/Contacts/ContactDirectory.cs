using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WaBulk.Domain.Contacts;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Contacts;

/// <summary>
/// Encuentra o crea el Contact de un teléfono y/o BSUID, y completa el mapeo BSUID ↔ teléfono cuando un mensaje
/// trae los dos. Ejemplo: el cliente escribe con su número (+591…9667, BO.188…); luego adopta un nombre de usuario y
/// Meta deja de mandar el número: su siguiente "BAJA" llega solo con BO.188…, y gracias al mapeo se aplica a +591…9667.
/// </summary>
public sealed partial class ContactDirectory(WaBulkDbContext db, TimeProvider clock, ILogger<ContactDirectory> logger)
{
    /// <summary>
    /// Devuelve el contacto (guardado: tiene Id real). Si lo crea o le completa datos, guarda en ese momento para que los
    /// mensajes y registros que lo referencian tengan su Id.
    /// </summary>
    public async Task<Contact> ResolveAsync(
        string? phoneE164, string? userId, string? profileName, string createdBy, CancellationToken cancellationToken)
    {
        if (phoneE164 is null && userId is null)
        {
            throw new ArgumentException("Se necesita teléfono o BSUID.");
        }

        var byPhone = phoneE164 is null ? null
            : db.Contacts.Local.FirstOrDefault(c => c.PhoneE164 == phoneE164)
              ?? await db.Contacts.FirstOrDefaultAsync(c => c.PhoneE164 == phoneE164, cancellationToken);
        var byUser = userId is null ? null
            : db.Contacts.Local.FirstOrDefault(c => c.UserId == userId)
              ?? await db.Contacts.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

        var contact = byPhone ?? byUser;
        var changed = false;
        if (contact is null)
        {
            contact = new Contact
            {
                PhoneE164 = phoneE164,
                WaId = phoneE164?.TrimStart('+'),
                UserId = userId,
                Name = Trim(profileName),
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
                CreatedBy = createdBy,
            };
            db.Contacts.Add(contact);
            changed = true;
        }
        else if (byPhone is not null && byUser is not null && byPhone.Id != byUser.Id)
        {
            // Dos contactos distintos (uno por teléfono, otro por BSUID): no se fusionan solos; se usa el del teléfono.
            LogConflict(logger, byPhone.Id, byUser.Id);
        }
        else
        {
            if (contact.UserId is null && userId is not null)
            {
                contact.UserId = userId;
                changed = true;
            }

            if (contact.PhoneE164 is null && phoneE164 is not null)
            {
                contact.PhoneE164 = phoneE164;
                contact.WaId = phoneE164.TrimStart('+');
                changed = true;
            }

            if (contact.Name is null && Trim(profileName) is { } name)
            {
                contact.Name = name;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return contact;
    }

    private static string? Trim(string? name)
    {
        var t = name?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length <= 200 ? t : t[..200];
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "El teléfono es del contacto {PhoneContactId} y el BSUID del contacto {UserContactId}: no se fusionan, se usa el del teléfono")]
    private static partial void LogConflict(ILogger logger, long phoneContactId, long userContactId);
}
