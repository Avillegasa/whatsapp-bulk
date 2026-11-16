namespace WaBulk.Webhooks;

/// <summary>Configuración de la sección "Webhooks".</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Tamaño máximo del cuerpo. Meta agrupa hasta 1000 actualizaciones por POST, por eso el margen.
    /// Por encima se responde 413 sin guardar.
    /// </summary>
    public long MaxBodyBytes { get; set; } = 5 * 1024 * 1024;
}
