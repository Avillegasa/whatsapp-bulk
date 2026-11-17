namespace WaBulk.Domain.Consent;

public enum PlatformPreferenceValue
{
    Stop,
    Resume,
}

public enum PlatformPreferenceOrigin
{
    /// <summary>Webhook user_preferences.</summary>
    Webhook,
    /// <summary>Error 131050: "This recipient has chosen to stop receiving marketing messages".</summary>
    Error131050,
}

/// <summary>
/// Capa plataforma (docs/ARQUITECTURA.md "Bajas"): lo que el usuario eligió en WhatsApp para los mensajes de
/// marketing. Separada del consentimiento de la empresa: un resume solo levanta un stop de plataforma.
/// El vigente es el de FechaUtc más reciente (la hora de Meta, porque los webhooks pueden llegar en desorden).
/// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/user_preferences
/// </summary>
public class PlatformPreference
{
    public long Id { get; set; }
    public required string PhoneE164 { get; set; }
    public string Category { get; set; } = PlatformCategories.MarketingMessages;
    public PlatformPreferenceValue Value { get; set; }
    public PlatformPreferenceOrigin Origin { get; set; }
    public DateTime RecordedUtc { get; set; }
}

public static class PlatformCategories
{
    public const string MarketingMessages = "marketing_messages";
}
