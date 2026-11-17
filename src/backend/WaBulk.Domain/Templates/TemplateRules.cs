using System.Text.RegularExpressions;

namespace WaBulk.Domain.Templates;

/// <summary>Plantilla de marketing que pide crear el operador (pantalla Plantillas, F1.5).</summary>
/// <param name="Name">Nombre en Meta: minúsculas, números y guion bajo.</param>
/// <param name="Language">Código de idioma: "es", "es_MX", "en_US".</param>
/// <param name="HeaderText">Encabezado de texto opcional (sin parámetros).</param>
/// <param name="BodyText">Cuerpo con parámetros con nombre: "Hola {{nombre}}, …".</param>
/// <param name="FooterText">Pie opcional (sin parámetros).</param>
/// <param name="Examples">Valor de ejemplo por parámetro (Meta lo exige para revisar la plantilla).</param>
public sealed record TemplateDraft(
    string? Name,
    string? Language,
    string? HeaderText,
    string? BodyText,
    string? FooterText,
    IReadOnlyDictionary<string, string>? Examples);

/// <summary>
/// Reglas de Meta para crear y enviar plantillas, verificadas en la documentación (2026-11-17):
/// - Nombre: ^[a-z0-9_]+$, máx. 512; único por idioma (si no, error 100 / 2388024).
///   https://developers.facebook.com/documentation/business-messaging/whatsapp/templates/template-management
/// - Encabezado de texto ≤ 60, cuerpo ≤ 1024, pie ≤ 60 (sin parámetros); botones de respuesta rápida: hasta 10, texto ≤ 25;
///   cada parámetro necesita un valor de ejemplo.
///   https://developers.facebook.com/documentation/business-messaging/whatsapp/templates/components
/// - Parámetros con nombre ({{nombre}}, parameter_format NAMED) o posicionales ({{1}}); al enviar, los posicionales van
///   en el orden en que aparecen en el texto.
///   https://developers.facebook.com/documentation/business-messaging/whatsapp/templates/overview
/// La intranet solo crea plantillas de marketing (D-009) con parámetros con nombre y los dos botones de D-012.
/// </summary>
public static partial class TemplateRules
{
    public const int NameMaxLength = 512;
    public const int HeaderMaxLength = 60;
    public const int BodyMaxLength = 1024;
    public const int FooterMaxLength = 60;
    public const int ButtonMaxLength = 25;
    public const int ExampleMaxLength = 200;

    /// <summary>Parámetros del texto en el orden en que aparecen, sin repetir: "Hola {{nombre}}" → ["nombre"].</summary>
    public static IReadOnlyList<string> Parameters(string? text) =>
        text is null ? [] : [.. ParameterRegex().Matches(text).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.Ordinal)];

    /// <summary>true si todos los parámetros son posicionales ({{1}}, {{2}}…).</summary>
    public static bool IsPositional(IReadOnlyList<string> parameters) =>
        parameters.Count > 0 && parameters.All(p => p.All(char.IsAsciiDigit));

    /// <summary>Valida la plantilla que pide crear el operador. Devuelve los errores en español (vacío = válida).</summary>
    public static IReadOnlyList<string> Validate(TemplateDraft draft, string interestButton, string optOutButton)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var errors = new List<string>();

        var name = draft.Name?.Trim() ?? "";
        if (name.Length is 0 or > NameMaxLength || !NameRegex().IsMatch(name))
        {
            errors.Add("El nombre solo admite minúsculas, números y guion bajo (por ejemplo, promo_diciembre).");
        }

        if (!LanguageRegex().IsMatch(draft.Language?.Trim() ?? ""))
        {
            errors.Add("Idioma inválido: usa un código como es, es_MX o en_US.");
        }

        CheckFixedText(errors, "encabezado", draft.HeaderText, HeaderMaxLength);
        CheckFixedText(errors, "pie", draft.FooterText, FooterMaxLength);

        var body = draft.BodyText?.Trim() ?? "";
        if (body.Length is 0 or > BodyMaxLength)
        {
            errors.Add($"El cuerpo es obligatorio y admite hasta {BodyMaxLength} caracteres.");
        }

        var parameters = Parameters(body);
        foreach (var p in parameters.Where(p => !ParamNameRegex().IsMatch(p)))
        {
            errors.Add($"Parámetro {{{{{p}}}}} inválido: usa un nombre en minúsculas, como {{{{nombre}}}}.");
        }

        var examples = draft.Examples ?? new Dictionary<string, string>();
        foreach (var p in parameters.Where(p => !examples.TryGetValue(p, out var v) || string.IsNullOrWhiteSpace(v)))
        {
            errors.Add($"Falta el valor de ejemplo de {{{{{p}}}}} (Meta lo pide para revisar la plantilla).");
        }

        foreach (var (p, v) in examples.Where(e => parameters.Contains(e.Key) && e.Value?.Trim().Length > ExampleMaxLength))
        {
            errors.Add($"El ejemplo de {{{{{p}}}}} admite hasta {ExampleMaxLength} caracteres.");
        }

        foreach (var label in new[] { interestButton, optOutButton })
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length > ButtonMaxLength)
            {
                errors.Add($"Botón \"{label}\" fuera de los límites de Meta (1 a {ButtonMaxLength} caracteres): revisar la configuración.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Idioma en el formato de la API de plantillas: los webhooks lo mandan con guion ("en-US") y la API con guion bajo
    /// ("en_US").
    /// </summary>
    public static string NormalizeLanguage(string? language) => (language ?? "").Trim().Replace('-', '_');

    /// <summary>Faltantes y sobrantes entre los parámetros de la plantilla y los valores dados para enviarla.</summary>
    public static (IReadOnlyList<string> Missing, IReadOnlyList<string> Unknown) CompareValues(
        IReadOnlyList<string> parameters, IReadOnlyCollection<string> given) =>
        ([.. parameters.Where(p => !given.Contains(p))], [.. given.Where(g => !parameters.Contains(g))]);

    private static void CheckFixedText(List<string> errors, string label, string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Trim().Length > max)
        {
            errors.Add($"El {label} admite hasta {max} caracteres.");
        }

        if (text.Contains("{{", StringComparison.Ordinal))
        {
            errors.Add($"El {label} no admite parámetros en esta versión.");
        }
    }

    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex ParameterRegex();

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ParamNameRegex();

    [GeneratedRegex("^[a-z]{2,3}(_[A-Z]{2})?$")]
    private static partial Regex LanguageRegex();
}
