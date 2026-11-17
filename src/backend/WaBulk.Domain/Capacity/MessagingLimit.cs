using System.Globalization;

namespace WaBulk.Domain.Capacity;

/// <summary>
/// Límite real de mensajería de Meta (F1.7, D-026): destinatarios únicos por 24 h fuera de la ventana de atención. Una
/// fila por portafolio, actualizada por GET /{PHONE_NUMBER_ID}?fields=whatsapp_business_manager_messaging_limit y por los
/// webhooks business_capability_update / phone_number_quality_update (max_daily_conversations_per_business). Mientras
/// no exista, la capacidad usa Capacity:PortfolioLimit.
/// </summary>
public class MessagingLimit
{
    public required string PortfolioId { get; set; }

    /// <summary>Destinatarios por 24 h. <see cref="int.MaxValue"/> = sin límite.</summary>
    public int Limit { get; set; }

    /// <summary>Valor tal como lo mandó Meta ("TIER_250", "2000"…).</summary>
    public required string MetaValue { get; set; }

    /// <summary>Api o Webhook.</summary>
    public required string Source { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Traduce los valores de Meta a un número de destinatarios.</summary>
public static class MessagingTiers
{
    /// <summary>
    /// "TIER_50" → 50, "TIER_250" → 250, "TIER_2K" → 2000, "TIER_10K" → 10000, "TIER_100K" → 100000,
    /// "TIER_UNLIMITED" o -1 → sin límite; números (el ejemplo del webhook manda 2000) se toman tal cual.
    /// "TIER_NOT_SET" o desconocido → null (se sigue usando la configuración).
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/business_capability_update
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/messaging-limits
    /// </summary>
    public static int? Parse(string? value)
    {
        var v = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        if (int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            return n == -1 ? int.MaxValue : n > 0 ? n : null;
        }

        return v switch
        {
            "TIER_UNLIMITED" => int.MaxValue,
            _ when v.StartsWith("TIER_", StringComparison.Ordinal) => ParseTier(v[5..]),
            _ => null,
        };
    }

    private static int? ParseTier(string t)
    {
        var multiplier = t.EndsWith('K') ? 1000 : 1;
        var digits = multiplier == 1 ? t : t[..^1];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n * multiplier : null;
    }
}
