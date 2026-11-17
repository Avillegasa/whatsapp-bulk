using System.Globalization;
using System.Text;

namespace WaBulk.Domain.Consent;

/// <summary>Motivo por el que un trabajo no se envía (DispatchJob.MotivoOmision).</summary>
public static class SkipReasons
{
    public const string SinConsentimiento = "SinConsentimiento";
    public const string BajaEmpresa = "BajaEmpresa";
    public const string NoContactar = "NoContactar";
    public const string BajaPlataforma = "BajaPlataforma";
}

public sealed record EligibilityResult(bool Eligible, string? SkipReason)
{
    public static readonly EligibilityResult Ok = new(true, null);
}

/// <summary>
/// ¿Se le puede enviar una promoción? Las dos capas son independientes (regla 8):
/// la empresa exige un OptIn vigente; la plataforma, que el último valor no sea stop.
/// Ejemplo: OptIn + stop + resume → elegible; OptIn + BAJA por palabra clave + resume → no elegible.
/// </summary>
public static class MarketingEligibility
{
    public static EligibilityResult Evaluate(ConsentStatus? company, PlatformPreferenceValue? platform) =>
        company switch
        {
            null => new(false, SkipReasons.SinConsentimiento),
            ConsentStatus.OptOut => new(false, SkipReasons.BajaEmpresa),
            ConsentStatus.NoContactar => new(false, SkipReasons.NoContactar),
            _ when platform == PlatformPreferenceValue.Stop => new(false, SkipReasons.BajaPlataforma),
            _ => EligibilityResult.Ok,
        };

    /// <summary>
    /// Una importación nunca levanta una baja de la empresa: solo un opt-in manual (auditado) o un registro de la
    /// intranet puede reactivar a quien pidió la baja.
    /// </summary>
    public static bool CanRecordOptIn(ConsentStatus? current, ConsentSource source) =>
        source != ConsentSource.Importacion || current is null or ConsentStatus.OptIn;
}

/// <summary>Detección de bajas en mensajes entrantes (docs/ARQUITECTURA.md "Bajas", capa empresa).</summary>
public static class OptOutDetector
{
    /// <summary>
    /// El mensaje completo debe ser una palabra clave (sin distinguir mayúsculas, tildes, espacios ni signos):
    /// "baja", " BAJA! " y "Bája." cuentan; "no quiero salir de aquí" no.
    /// </summary>
    public static bool IsKeyword(string? text, IEnumerable<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        var normalized = Normalize(text);
        return normalized.Length > 0 && keywords.Any(k => Normalize(k) == normalized);
    }

    /// <summary>Botón de baja: coincide el payload o el texto con los configurados (sin distinguir mayúsculas ni tildes).</summary>
    public static bool IsOptOutButton(string? payload, string? text, IEnumerable<string> payloads, IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(texts);
        var p = Normalize(payload);
        var t = Normalize(text);
        return (p.Length > 0 && payloads.Any(x => Normalize(x) == p))
            || (t.Length > 0 && texts.Any(x => Normalize(x) == t));
    }

    /// <summary>Mayúsculas, sin tildes, sin signos y con un solo espacio entre palabras.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().TrimEnd();
    }
}
