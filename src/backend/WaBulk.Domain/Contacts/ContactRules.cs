using WaBulk.Domain.Consent;

namespace WaBulk.Domain.Contacts;

/// <summary>Teléfonos en E.164 (ARQUITECTURA: Bolivia = 591 + 8 dígitos).</summary>
public static class PhoneNumbers
{
    public const string BoliviaPrefix = "591";

    /// <summary>
    /// Normaliza lo que escribe una persona en una planilla. Acepta espacios, guiones, puntos y paréntesis.
    /// Ejemplos: "70000000" → "+59170000000"; "591 7000-0000" → "+59170000000"; "+54 9 11 1234 5678" → "+5491112345678".
    /// Un número boliviano debe ser celular (8 dígitos que empiezan con 6 o 7): WhatsApp no llega a un fijo.
    /// </summary>
    public static bool TryNormalize(string? input, out string e164, out string error)
    {
        e164 = "";
        var raw = (input ?? "").Trim();
        if (raw.Length == 0)
        {
            error = "Teléfono vacío.";
            return false;
        }

        var plus = raw.StartsWith('+');
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (raw.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-' or '.' or '(' or ')' or '+'))
            || raw.LastIndexOf('+') > 0)
        {
            error = $"\"{raw}\" tiene caracteres que no son de un teléfono.";
            return false;
        }

        if (!plus && digits.Length == 8)
        {
            digits = BoliviaPrefix + digits; // Número local boliviano.
        }

        if (digits.StartsWith(BoliviaPrefix, StringComparison.Ordinal))
        {
            var local = digits[BoliviaPrefix.Length..];
            if (local.Length != 8 || local[0] is not ('6' or '7'))
            {
                error = $"\"{raw}\" no es un celular boliviano (591 + 8 dígitos que empiezan con 6 o 7).";
                return false;
            }
        }
        else if (!plus)
        {
            error = $"\"{raw}\": sin código de país escribe 8 dígitos (Bolivia) o empieza con + (otro país).";
            return false;
        }

        // E.164: hasta 15 dígitos, sin 0 inicial en el código de país.
        if (digits.Length is < 8 or > 15 || digits[0] == '0')
        {
            error = $"\"{raw}\" no es un número E.164 válido.";
            return false;
        }

        e164 = "+" + digits;
        error = "";
        return true;
    }

    /// <summary>wa_id de Meta ("59170000000") → "+59170000000".</summary>
    public static string? FromWaId(string? waId) =>
        string.IsNullOrWhiteSpace(waId) ? null : waId.StartsWith('+') ? waId : "+" + waId;
}

/// <summary>Por qué no se puede ofrecer promociones a un contacto (D-031).</summary>
public static class OfferRefusals
{
    public const string SinVentana = "SinVentana";
    public const string YaTieneOptIn = "YaTieneOptIn";
    public const string NoContactar = "NoContactar";
    public const string RechazoReciente = "RechazoReciente";
    public const string OfertaPendiente = "OfertaPendiente";
    public const string ContactoInactivo = "ContactoInactivo";
    public const string NoExiste = "NoExiste";
    /// <summary>Solo BSUID: el consentimiento se registra por teléfono (D-025), así que no se ofrece.</summary>
    public const string SinTelefono = "SinTelefono";
}

/// <summary>Reglas para ofrecer promociones (D-031). Puras: sin base ni reloj, para probarlas con ejemplos.</summary>
public static class ConsentOfferRules
{
    /// <summary>Duración de la ventana de atención de Meta: 24 h desde el último mensaje del cliente.</summary>
    public static readonly TimeSpan ServiceWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// ¿La ventana sigue abierta? Se descuenta un margen para no enviar justo cuando cierra.
    /// Ejemplo: último mensaje 10:00 del lunes, margen 10 min → abierta hasta las 9:50 del martes.
    /// </summary>
    public static bool IsWindowOpen(DateTime? lastInboundUtc, DateTime nowUtc, TimeSpan margin) =>
        lastInboundUtc is { } last && nowUtc < last + ServiceWindow - margin;

    /// <summary>Devuelve null si se puede ofrecer, o el motivo (<see cref="OfferRefusals"/>).</summary>
    /// <param name="lastDeclinedUtc">Última vez que respondió «Ahora no».</param>
    /// <param name="pendingOfferUtc">Oferta enviada sin respuesta (para no mandarla dos veces con un doble clic).</param>
    public static string? Check(
        bool contactActive, bool windowOpen, ConsentStatus? currentConsent, DateTime? lastDeclinedUtc,
        DateTime? pendingOfferUtc, DateTime nowUtc, TimeSpan reofferAfter)
    {
        if (!contactActive)
        {
            return OfferRefusals.ContactoInactivo;
        }

        if (currentConsent == ConsentStatus.NoContactar)
        {
            return OfferRefusals.NoContactar;
        }

        if (currentConsent == ConsentStatus.OptIn)
        {
            return OfferRefusals.YaTieneOptIn;
        }

        if (lastDeclinedUtc is { } declined && nowUtc < declined + reofferAfter)
        {
            return OfferRefusals.RechazoReciente;
        }

        if (pendingOfferUtc is { } pending && nowUtc < pending + ServiceWindow)
        {
            return OfferRefusals.OfertaPendiente;
        }

        // La ventana va al final: es lo único que el encargado no puede resolver esperando ni preguntando.
        return windowOpen ? null : OfferRefusals.SinVentana;
    }
}

/// <summary>Ids de los botones del mensaje de oferta (D-031). Fijos: el procesador los reconoce en la respuesta.</summary>
public static class OfferButtons
{
    public const string Si = "OPTIN_PROMOS";
    public const string AhoraNo = "OPTIN_AHORA_NO";
}
