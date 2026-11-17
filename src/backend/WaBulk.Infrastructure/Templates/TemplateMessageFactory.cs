using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Templates;

/// <summary>
/// Arma el <see cref="TemplateMessage"/> de una plantilla de la tabla con los valores de sus parámetros (F1.5): exige que
/// esté APPROVED y que los valores coincidan con los parámetros del cuerpo. Lo usa send-test; en F1.6 lo usará el
/// despachador con los parámetros de la versión de campaña.
/// </summary>
public static class TemplateMessageFactory
{
    public static async Task<(TemplateMessage? Message, string? Error)> BuildAsync(
        WaBulkDbContext db, string name, string? language, IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(values);
        var lang = language is null ? null : TemplateRules.NormalizeLanguage(language);
        var candidates = await db.Templates.AsNoTracking()
            .Where(t => t.Name == name && (lang == null || t.Language == lang))
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return (null, $"La plantilla {name}{(lang is null ? "" : $" ({lang})")} no está en la tabla Template: sincroniza desde la pantalla Plantillas.");
        }

        if (candidates.Count > 1)
        {
            return (null, $"La plantilla {name} existe en varios idiomas ({string.Join(", ", candidates.Select(c => c.Language))}): indica --lang.");
        }

        var template = candidates[0];
        if (template.Status != TemplateStatuses.Approved)
        {
            return (null, $"La plantilla {name} ({template.Language}) está {template.Status}: solo se envían plantillas APPROVED.");
        }

        var parameters = TemplateContent.Parse(template.Components).BodyParameters;
        var (missing, unknown) = TemplateRules.CompareValues(parameters, [.. values.Keys]);
        if (missing.Count > 0 || unknown.Count > 0)
        {
            return (null, $"Parámetros de {name}: {(parameters.Count == 0 ? "ninguno" : string.Join(", ", parameters))}."
                          + (missing.Count > 0 ? $" Faltan: {string.Join(", ", missing)}." : "")
                          + (unknown.Count > 0 ? $" Sobran: {string.Join(", ", unknown)}." : ""));
        }

        var empty = parameters.Where(p => string.IsNullOrWhiteSpace(values[p])).ToList();
        if (empty.Count > 0)
        {
            return (null, $"Parámetros vacíos: {string.Join(", ", empty)} (Meta no acepta valores vacíos).");
        }

        var named = !string.Equals(template.ParameterFormat, "POSITIONAL", StringComparison.OrdinalIgnoreCase)
                    && !TemplateRules.IsPositional(parameters);
        return (new TemplateMessage(template.Name, template.Language,
            [.. parameters.Select(p => new TemplateParameter(p, values[p].Trim()))], named), null);
    }
}
