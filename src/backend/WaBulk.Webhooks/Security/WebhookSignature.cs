using System.Security.Cryptography;
using System.Text;

namespace WaBulk.Webhooks.Security;

/// <summary>
/// Firma X-Hub-Signature-256 de Meta: "sha256=" + HMAC-SHA256 en hex del cuerpo, con el App Secret como clave.
/// Se calcula sobre los bytes crudos tal como llegaron (nunca sobre JSON re-serializado) y se compara
/// en tiempo constante (regla 6).
/// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/create-webhook-endpoint
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Hub-Signature-256";
    private const string Prefix = "sha256=";
    private const int HashHexLength = 64;

    public static bool IsValid(ReadOnlySpan<byte> body, string? header, string appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || header is null
            || !header.StartsWith(Prefix, StringComparison.Ordinal) || header.Length != Prefix.Length + HashHexLength)
        {
            return false;
        }

        byte[] received;
        try
        {
            received = Convert.FromHexString(header.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }

    /// <summary>Calcula el valor del encabezado (para pruebas y para la herramienta de replay, F0.8).</summary>
    public static string Compute(ReadOnlySpan<byte> body, string appSecret) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body));
}
