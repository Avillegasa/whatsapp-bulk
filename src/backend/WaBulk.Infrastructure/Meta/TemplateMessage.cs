namespace WaBulk.Infrastructure.Meta;

/// <summary>Plantilla a enviar. Los parámetros (components) se agregan con las campañas (F0.9 / Fase 1).</summary>
/// <param name="Name">Nombre de la plantilla aprobada, por ejemplo "hello_world".</param>
/// <param name="LanguageCode">Código de idioma de la plantilla, por ejemplo "en_US" o "es".</param>
public sealed record TemplateMessage(string Name, string LanguageCode);
