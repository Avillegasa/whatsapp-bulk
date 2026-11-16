namespace WaBulk.Infrastructure.Meta;

/// <summary>
/// Configuración de la sección "Meta". Los IDs van en appsettings; AccessToken, AppSecret y VerifyToken
/// solo en dotnet user-secrets (desarrollo) o variables de entorno META__* (Docker). Regla 10 de CLAUDE.md.
/// </summary>
public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    /// <summary>Versión de Graph API, por ejemplo "v27.0". Nunca literal en el código (regla 2).</summary>
    public string GraphApiVersion { get; set; } = "";

    public Uri BaseUrl { get; set; } = new("https://graph.facebook.com/");

    public string PhoneNumberId { get; set; } = "";

    public string WabaId { get; set; } = "";

    /// <summary>Token del System User. Secreto.</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>App Secret: clave del HMAC de X-Hub-Signature-256. Secreto.</summary>
    public string AppSecret { get; set; } = "";

    /// <summary>Cadena inventada para el handshake GET del webhook. Secreto.</summary>
    public string VerifyToken { get; set; } = "";

    /// <summary>Tiempo máximo de una llamada; si se supera, el intento queda en SubmissionUnknown.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}
