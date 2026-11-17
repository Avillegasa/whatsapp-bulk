namespace WaBulk.Infrastructure.Meta;

/// <summary>Plantilla a enviar, con los valores de los parámetros del cuerpo (F1.5).</summary>
/// <param name="Name">Nombre de la plantilla aprobada, por ejemplo "hello_world".</param>
/// <param name="LanguageCode">Código de idioma de la plantilla, por ejemplo "en_US" o "es".</param>
/// <param name="BodyParameters">
/// Valores del cuerpo en el orden en que aparecen en el texto. Con <paramref name="NamedParameters"/> se envía también
/// parameter_name; sin él (plantilla POSITIONAL) solo el orden cuenta. Vacío o null: la plantilla no lleva components.
/// </param>
/// <param name="NamedParameters">true si la plantilla es parameter_format NAMED.</param>
public sealed record TemplateMessage(
    string Name,
    string LanguageCode,
    IReadOnlyList<TemplateParameter>? BodyParameters = null,
    bool NamedParameters = true);

/// <summary>Un parámetro de texto del cuerpo: {{nombre}} → ("nombre", "Andrés").</summary>
public sealed record TemplateParameter(string Name, string Value);
