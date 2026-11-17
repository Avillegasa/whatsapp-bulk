namespace WaBulk.Domain.Consent;

/// <summary>Estado del consentimiento de la empresa para una finalidad.</summary>
public enum ConsentStatus
{
    OptIn,
    OptOut,
    /// <summary>No contactar por ninguna finalidad (pedido expreso o decisión de la empresa).</summary>
    NoContactar,
}

/// <summary>Origen de un registro de consentimiento.</summary>
public enum ConsentSource
{
    /// <summary>Botón de baja de una plantilla ("No recibir más").</summary>
    Boton,
    /// <summary>Mensaje con una palabra clave (BAJA, STOP, SALIR…).</summary>
    PalabraClave,
    /// <summary>Pedido fuera de WhatsApp registrado en la intranet.</summary>
    Intranet,
    /// <summary>Importación de contactos (Fase 1): nunca levanta una baja.</summary>
    Importacion,
    /// <summary>Registro manual auditado (por ejemplo, un nuevo opt-in).</summary>
    Manual,
}

/// <summary>
/// Capa empresa (docs/ARQUITECTURA.md "Bajas"): historial inmutable; el estado vigente es el último registro por
/// teléfono y finalidad. Se identifica por PhoneE164 porque Contact llega en la Fase 1 (D-025); ContactId queda
/// opcional hasta entonces.
/// </summary>
public class ConsentRecord
{
    public long Id { get; set; }
    public long? ContactId { get; set; }
    public required string PhoneE164 { get; set; }
    public string Purpose { get; set; } = ConsentPurposes.Promociones;
    public string Channel { get; set; } = "WhatsApp";
    public ConsentStatus Status { get; set; }
    public ConsentSource Source { get; set; }
    /// <summary>Texto del mensaje, payload del botón o referencia del pedido.</summary>
    public string? Evidence { get; set; }
    /// <summary>wamid del mensaje entrante que originó la baja (idempotencia del procesador).</summary>
    public string? SourceWamid { get; set; }
    public DateTime RecordedUtc { get; set; }
    public required string RecordedBy { get; set; }
}

public static class ConsentPurposes
{
    /// <summary>Única finalidad de la primera etapa (regla 12).</summary>
    public const string Promociones = "Promociones";
}
