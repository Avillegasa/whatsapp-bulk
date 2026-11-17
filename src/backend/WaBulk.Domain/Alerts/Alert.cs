namespace WaBulk.Domain.Alerts;

/// <summary>Tipos de alerta del freno de emergencia (F0.12). Se guardan como texto.</summary>
public enum AlertType
{
    /// <summary>Error 190: el token de acceso venció o es inválido.</summary>
    Token,
    /// <summary>Error 131042: problema con el método de pago de la cuenta.</summary>
    Facturacion,
    /// <summary>Plantilla pausada por Meta (webhook PAUSED o error 132015 al enviar).</summary>
    PlantillaPausada,

    /// <summary>F1.7 · 131048: Meta restringe cuántos mensajes puede enviar el número (calidad).</summary>
    Calidad,

    /// <summary>F1.7 · 131031 o 368: la cuenta fue restringida o desactivada por infringir una política.</summary>
    Politica,

    /// <summary>F1.7 · más del 5 % de fallos en los últimos 200 envíos de campañas.</summary>
    TasaFallos,
}

/// <summary>
/// Alerta registrada (tabla Alert). Una sola abierta por (Tipo, Clave): si se repite, suma Ocurrencias y actualiza
/// UltimaUtc. Se resuelve al levantar la pausa.
/// </summary>
public class Alert
{
    public long Id { get; set; }
    public AlertType Type { get; set; }
    /// <summary>Qué la produjo dentro del tipo: "global" o "nombre:idioma" de la plantilla.</summary>
    public required string Key { get; set; }
    public required string Message { get; set; }
    public string? Detail { get; set; }
    /// <summary>Despachador o Webhook.</summary>
    public required string Source { get; set; }
    public int Occurrences { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public DateTime LastUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public string? ResolvedBy { get; set; }
}

/// <summary>
/// Pausa del despacho (tabla DispatchPause). Mientras haya una pausa global activa (LevantadaUtc nula) el despachador
/// no reclama trabajos. Solo una persona la levanta (comando reanudar).
/// </summary>
public class DispatchPause
{
    public long Id { get; set; }
    public string Scope { get; set; } = PauseScopes.Global;
    public AlertType Reason { get; set; }
    public long AlertId { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime? LiftedUtc { get; set; }
    public string? LiftedBy { get; set; }
}

public static class PauseScopes
{
    public const string Global = "Global";
}

/// <summary>Errores de Meta que frenan todo el despacho (docs/ARQUITECTURA.md "Errores" y "Freno de emergencia").</summary>
public static class EmergencyBrake
{
    /// <summary>
    /// 190 token, 131042 facturación, 132015 plantilla pausada; F1.7: 131048 (límite por calidad del número), 131031 y
    /// 368 (cuenta restringida por política). Ninguno es culpa del destinatario: el trabajo vuelve a la cola y sale cuando
    /// una persona levanta la pausa (no es un reintento automático).
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/support/error-codes
    /// </summary>
    public static AlertType? ForErrorCode(int? code) => code switch
    {
        190 => AlertType.Token,
        131042 => AlertType.Facturacion,
        132015 => AlertType.PlantillaPausada,
        131048 => AlertType.Calidad,
        131031 or 368 => AlertType.Politica,
        _ => null,
    };

    /// <summary>
    /// ¿Superan los fallos el umbral? Solo con una muestra mínima (con pocos envíos, un fallo daría un porcentaje alto
    /// sin significar nada).
    /// </summary>
    public static bool FailureRateExceeded(int total, int failures, int minSample, int thresholdPercent) =>
        total >= minSample && failures * 100 > thresholdPercent * total;
}
