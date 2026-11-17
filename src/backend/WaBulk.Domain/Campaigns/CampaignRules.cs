using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WaBulk.Domain.Campaigns;

/// <summary>
/// Reglas de campañas (F1.6): qué transiciones se permiten, qué se valida al crear una versión, cómo se resuelven los
/// parámetros por contacto y cómo se calcula el Hash de una versión.
/// </summary>
public static class CampaignRules
{
    /// <summary>Largo máximo propio de un valor de parámetro (Meta no publica uno; 132012 = formato incorrecto).</summary>
    public const int ParameterMaxLength = 500;

    /// <summary>Plazo máximo entre la programación y el vencimiento (propio: una promoción no debería salir semanas tarde).</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static bool CanSubmit(CampaignStatus s) => s == CampaignStatus.Draft;

    public static bool CanDecide(CampaignStatus s) => s == CampaignStatus.PendingApproval;

    /// <summary>Editar = crear una versión nueva. No en estados finales.</summary>
    public static bool CanEdit(CampaignStatus s) =>
        s is CampaignStatus.Draft or CampaignStatus.PendingApproval or CampaignStatus.Scheduled or CampaignStatus.Running
            or CampaignStatus.Paused;

    public static bool CanPause(CampaignStatus s) => s is CampaignStatus.Scheduled or CampaignStatus.Running;

    public static bool CanResume(CampaignStatus s) => s == CampaignStatus.Paused;

    public static bool CanCancel(CampaignStatus s) => CanEdit(s);

    /// <summary>Estados en los que el despachador puede enviar trabajos de la campaña.</summary>
    public static bool IsSending(CampaignStatus s) => s is CampaignStatus.Scheduled or CampaignStatus.Running;

    /// <summary>
    /// Valida el contenido de una versión nueva. <paramref name="templateParameters"/> son los parámetros del cuerpo de la
    /// plantilla en orden; cada uno debe tener exactamente una definición con valor.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<string> templateParameters, IReadOnlyList<CampaignParameter> parameters, DateTime scheduledUtc,
        DateTime expiresUtc, DateTime nowUtc, int audienceCount)
    {
        ArgumentNullException.ThrowIfNull(templateParameters);
        ArgumentNullException.ThrowIfNull(parameters);
        var errors = new List<string>();

        var given = parameters.Select(p => p.Name).ToList();
        foreach (var missing in templateParameters.Where(p => !given.Contains(p)))
        {
            errors.Add($"Falta el valor de {{{{{missing}}}}}.");
        }

        foreach (var extra in given.Where(g => !templateParameters.Contains(g)).Distinct())
        {
            errors.Add($"La plantilla no tiene el parámetro {{{{{extra}}}}}.");
        }

        foreach (var repeated in given.GroupBy(g => g).Where(g => g.Count() > 1))
        {
            errors.Add($"El parámetro {{{{{repeated.Key}}}}} está repetido.");
        }

        foreach (var p in parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Value))
            {
                errors.Add(p.Source == ParameterSource.NombreContacto
                    ? $"{{{{{p.Name}}}}}: falta el texto para contactos sin nombre."
                    : $"{{{{{p.Name}}}}}: el valor no puede estar vacío.");
            }
            else if (p.Value.Trim().Length > ParameterMaxLength)
            {
                errors.Add($"{{{{{p.Name}}}}}: hasta {ParameterMaxLength} caracteres.");
            }
        }

        if (expiresUtc <= scheduledUtc)
        {
            errors.Add("El vencimiento debe ser posterior a la programación.");
        }
        else if (expiresUtc - scheduledUtc > MaxWindow)
        {
            errors.Add($"Entre la programación y el vencimiento puede haber como máximo {MaxWindow.TotalDays:0} días.");
        }

        if (expiresUtc <= nowUtc)
        {
            errors.Add("El vencimiento ya pasó.");
        }

        if (audienceCount == 0)
        {
            errors.Add("La lista no tiene contactos con teléfono activos.");
        }

        return errors;
    }

    /// <summary>Valores de un contacto en el orden de la plantilla: {{nombre}} = "Ana" (o el texto por defecto).</summary>
    public static IReadOnlyList<(string Name, string Value)> Resolve(
        IReadOnlyList<CampaignParameter> parameters, string? contactName)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return [.. parameters.Select(p => (p.Name, p.Source == ParameterSource.NombreContacto && !string.IsNullOrWhiteSpace(contactName)
            ? FirstName(contactName)
            : p.Value.Trim()))];
    }

    /// <summary>"Andrés Villegas" → "Andrés": en un saludo suena natural y evita nombres largos.</summary>
    public static string FirstName(string name) => name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>DispatchJob.Parametros: [{"Name":"nombre","Value":"Ana"}…] en el orden del cuerpo.</summary>
    public static string SerializeJobParameters(IReadOnlyList<(string Name, string Value)> values) =>
        JsonSerializer.Serialize(values.Select(v => new JobParameter(v.Name, v.Value)).ToArray(), Json);

    public static IReadOnlyList<JobParameter> DeserializeJobParameters(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<JobParameter[]>(json, Json) ?? [];

    public static string SerializeParameters(IReadOnlyList<CampaignParameter> parameters) => JsonSerializer.Serialize(parameters, Json);

    public static IReadOnlyList<CampaignParameter> DeserializeParameters(string json) =>
        JsonSerializer.Deserialize<List<CampaignParameter>>(json, Json) ?? [];

    public static string SerializeAudience(IEnumerable<long> contactIds) => JsonSerializer.Serialize(contactIds.Order().ToArray(), Json);

    public static IReadOnlyList<long> DeserializeAudience(string json) => JsonSerializer.Deserialize<long[]>(json, Json) ?? [];

    /// <summary>
    /// Hash del contenido de la versión (SHA-256, hex): plantilla, idioma, categoría, formato, parámetros, audiencia,
    /// programación y vencimiento. Dos versiones con el mismo contenido tienen el mismo Hash.
    /// </summary>
    public static string ComputeHash(CampaignVersion v)
    {
        ArgumentNullException.ThrowIfNull(v);
        var canonical = string.Join("\n",
            v.TemplateId.ToString(CultureInfo.InvariantCulture), v.TemplateName, v.Language, v.Category, v.ParameterFormat,
            v.Parameters, v.Audience,
            Utc(v.ScheduledUtc), Utc(v.ExpiresUtc));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Fecha UTC en texto fijo. SQL Server devuelve datetime2 sin Kind (Unspecified): sin esto, el Hash de una versión
    /// leída de la base no coincidiría con el calculado al crearla.
    /// </summary>
    private static string Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
