using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Contacts;

/// <summary>Sección "OfertaPromociones" (D-031): texto versionado del mensaje y reglas de la oferta.</summary>
public sealed class PromotionOfferOptions
{
    public const string SectionName = "OfertaPromociones";

    /// <summary>Cambiarla cada vez que cambie el texto: queda en la evidencia del OptIn.</summary>
    public string TextVersion { get; set; } = "v1";

    /// <summary>Cuerpo del mensaje; {N} se reemplaza por <see cref="MaxPromotionsPerWeek"/>.</summary>
    public string Text { get; set; } =
        "¿Quieres que te enviemos por este WhatsApp nuestras ofertas y promociones? Enviaremos como máximo {N} " +
        "promociones por semana. Puedes dejar de recibirlas cuando quieras escribiendo BAJA.";

    public string YesTitle { get; set; } = "Sí, quiero ofertas";
    public string NotNowTitle { get; set; } = "Ahora no";

    /// <summary>Lo promete el texto; el despachador lo cumple (F1.7, Skipped LimiteFrecuencia).</summary>
    public int MaxPromotionsPerWeek { get; set; } = 2;

    /// <summary>Tras «Ahora no» no se vuelve a ofrecer durante estos días.</summary>
    public int ReofferAfterDays { get; set; } = 30;

    /// <summary>Margen antes de que cierre la ventana de 24 h.</summary>
    public int WindowMarginMinutes { get; set; } = 10;

    public string RenderedText => Text.Replace("{N}", MaxPromotionsPerWeek.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>Límites de Meta para botones de respuesta: título ≤ 20, distintos; cuerpo ≤ 1024.</summary>
    public bool IsValid(out string error)
    {
        error = YesTitle.Length is 0 or > 20 || NotNowTitle.Length is 0 or > 20 ? "Los títulos de los botones deben tener entre 1 y 20 caracteres."
            : string.Equals(YesTitle, NotNowTitle, StringComparison.OrdinalIgnoreCase) ? "Los títulos de los botones deben ser distintos."
            : RenderedText.Length is 0 or > 1024 ? "El texto debe tener entre 1 y 1024 caracteres."
            : string.IsNullOrWhiteSpace(TextVersion) || TextVersion.Length > 40 ? "TextVersion es obligatoria (máx. 40)."
            : MaxPromotionsPerWeek < 1 || ReofferAfterDays < 0 || WindowMarginMinutes is < 0 or > 600 ? "Valores fuera de rango."
            : "";
        return error.Length == 0;
    }
}

public sealed record OfferResult(bool Sent, string? Refusal, string Message, ConsentOffer? Offer);

/// <summary>Una conversación reciente para la pantalla "Mensajes recientes": último mensaje del cliente y si se le puede ofrecer.</summary>
public sealed record RecentConversation(
    long ContactId, string? PhoneE164, string? Name, string LastType, string? LastText, DateTime LastUtc,
    DateTime WindowOpenUntilUtc, string Consent, string? Refusal, string? RefusalMessage, string? LastOfferStatus,
    string? LastOfferResponse);

/// <summary>
/// "Ofrecer promociones" (D-031): revisa las reglas, guarda la oferta y su AuditLog (commit), y recién hace un solo
/// POST del mensaje interactivo, como SendAttempt (regla 3). biz_opaque_callback_data = "oferta:{Id}" para reconocer
/// sus estados en el procesador.
/// </summary>
public sealed class ConsentOfferService(
    WaBulkDbContext db,
    ConsentService consent,
    MetaWhatsAppClient meta,
    IOptions<PromotionOfferOptions> options,
    TimeProvider clock)
{
    public const string CallbackPrefix = "oferta:";

    /// <summary>Estado de la oferta para un contacto: cuándo escribió por última vez y si hoy se le puede ofrecer.</summary>
    public async Task<(DateTime? LastInboundUtc, ConsentStatus? Consent, string? Refusal)> EvaluateAsync(
        Contact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var lastInbound = await db.InboundMessages.AsNoTracking().Where(m => m.ContactId == contact.Id)
            .MaxAsync(m => (DateTime?)m.SentUtc, cancellationToken);
        var current = contact.PhoneE164 is null ? null : await consent.CurrentCompanyStatusAsync(contact.PhoneE164, cancellationToken);
        var offers = db.ConsentOffers.AsNoTracking().Where(x => x.ContactId == contact.Id);
        var lastDeclined = await offers.Where(x => x.Response == ConsentOfferResponse.AhoraNo)
            .MaxAsync(x => x.ResponseUtc, cancellationToken);
        var pending = await offers.Where(x => x.Response == null && x.Status != ConsentOfferStatus.Rechazada)
            .MaxAsync(x => (DateTime?)x.OfferedUtc, cancellationToken);

        var refusal = contact.PhoneE164 is null
            ? OfferRefusals.SinTelefono
            : ConsentOfferRules.Check(contact.IsActive,
                ConsentOfferRules.IsWindowOpen(lastInbound, now, TimeSpan.FromMinutes(o.WindowMarginMinutes)),
                current, lastDeclined, pending, now, TimeSpan.FromDays(o.ReofferAfterDays));
        return (lastInbound, current, refusal);
    }

    /// <summary>Contactos que escribieron en los últimos <paramref name="days"/> días, el más reciente primero (máx. 100).</summary>
    public async Task<IReadOnlyList<RecentConversation>> RecentAsync(int days, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-days);
        // Último mensaje de cada contacto: el que no tiene otro más nuevo del mismo contacto (NOT EXISTS; GroupBy + First
        // seguido de OrderBy/Take no lo traduce EF Core 10).
        var recent = db.InboundMessages.AsNoTracking().Where(m => m.SentUtc >= since);
        var latest = await recent
            .Where(m => !recent.Any(n => n.ContactId == m.ContactId && (n.SentUtc > m.SentUtc || (n.SentUtc == m.SentUtc && n.Id > m.Id))))
            .OrderByDescending(m => m.SentUtc).Take(100)
            .ToListAsync(cancellationToken);
        var ids = latest.Select(m => m.ContactId).ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var offers = db.ConsentOffers.AsNoTracking().Where(x => ids.Contains(x.ContactId));
        var lastOffers = await offers
            .Where(x => !offers.Any(y => y.ContactId == x.ContactId && (y.OfferedUtc > x.OfferedUtc || (y.OfferedUtc == x.OfferedUtc && y.Id > x.Id))))
            .ToDictionaryAsync(x => x.ContactId, cancellationToken);

        var result = new List<RecentConversation>(latest.Count);
        foreach (var m in latest)
        {
            var contact = contacts[m.ContactId];
            var (_, consentStatus, refusal) = await EvaluateAsync(contact, cancellationToken);
            lastOffers.TryGetValue(contact.Id, out var offer);
            result.Add(new RecentConversation(
                contact.Id, contact.PhoneE164, contact.Name, m.Type, m.Text, m.SentUtc,
                m.SentUtc + ConsentOfferRules.ServiceWindow - TimeSpan.FromMinutes(o.WindowMarginMinutes),
                ContactConsentStates.From(consentStatus), refusal, refusal is null ? null : Describe(refusal),
                offer?.Status.ToString(), offer?.Response?.ToString()));
        }

        return result;
    }

    public async Task<OfferResult> OfferAsync(string actor, long contactId, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contactId, cancellationToken);
        if (contact is null)
        {
            return new(false, OfferRefusals.NoExiste, "El contacto no existe.", null);
        }

        var (_, _, refusal) = await EvaluateAsync(contact, cancellationToken);
        if (refusal is not null)
        {
            return new(false, refusal, Describe(refusal), null);
        }

        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var offer = new ConsentOffer
        {
            ContactId = contact.Id,
            PhoneE164 = contact.PhoneE164,
            TextVersion = o.TextVersion,
            Text = o.RenderedText,
            OfferedBy = actor,
            OfferedUtc = now,
        };

        // 1) Oferta y auditoría confirmadas ANTES del POST: si el proceso cae durante el POST, queda "Enviando" y
        //    nadie la reenvía sola.
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            db.ConsentOffers.Add(offer);
            await db.SaveChangesAsync(cancellationToken);
            db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.OfrecerPromociones, "Contacto",
                contact.Id.ToString(CultureInfo.InvariantCulture), null,
                new { oferta = offer.Id, version = offer.TextVersion, telefono = contact.PhoneE164 }, now));
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        // 2) Un solo POST.
        var result = await meta.SendReplyButtonsAsync(contact.PhoneE164, contact.UserId, offer.Text,
            [(OfferButtons.Si, o.YesTitle), (OfferButtons.AhoraNo, o.NotNowTitle)],
            CallbackPrefix + offer.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);

        offer.Status = result.Status switch
        {
            SendAttemptStatus.Accepted => ConsentOfferStatus.Enviada,
            SendAttemptStatus.Rejected => ConsentOfferStatus.Rechazada,
            _ => ConsentOfferStatus.Incierta,
        };
        offer.Wamid = result.Wamid;
        offer.HttpStatus = result.HttpStatus;
        offer.ErrorCode = result.ErrorCode;
        offer.Detail = result.Detail;
        await db.SaveChangesAsync(CancellationToken.None);

        return offer.Status switch
        {
            ConsentOfferStatus.Enviada => new(true, null, "Oferta enviada. Cuando el cliente responda, su consentimiento se actualiza solo.", offer),
            ConsentOfferStatus.Rechazada => new(false, null, $"Meta rechazó el mensaje (error {result.ErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "sin código"}).", offer),
            _ => new(false, null, "No hubo respuesta confiable de Meta: no se reenvía. Si el cliente recibe el mensaje y responde, se registra igual.", offer),
        };
    }

    public static string Describe(string refusal) => refusal switch
    {
        OfferRefusals.SinVentana => "El cliente no escribió en las últimas 24 h: fuera de la ventana solo se pueden enviar plantillas.",
        OfferRefusals.YaTieneOptIn => "El contacto ya acepta promociones.",
        OfferRefusals.NoContactar => "El contacto está marcado como No contactar.",
        OfferRefusals.RechazoReciente => "El cliente respondió «Ahora no» hace poco: no se vuelve a ofrecer todavía.",
        OfferRefusals.OfertaPendiente => "Ya se le ofreció y todavía no respondió.",
        OfferRefusals.ContactoInactivo => "El contacto está desactivado.",
        OfferRefusals.SinTelefono => "El contacto no tiene teléfono (solo nombre de usuario de WhatsApp): su consentimiento no se puede registrar.",
        _ => refusal,
    };
}
