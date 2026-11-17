using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WaBulk.WebhookReplay;

/// <summary>Valores que se sustituyen en las plantillas de payload (marcadores {{...}}).</summary>
public sealed record ReplayContext(
    string WabaId,
    string PhoneNumberId,
    string DisplayPhoneNumber,
    string WaId,
    string Wamid,
    Guid AttemptId,
    long BaseTimestamp,
    string InboundWamid = "wamid.REPLAYIN");

/// <summary>Un POST del escenario: plantilla de payload (payloads/*.json) y etiqueta para la consola.</summary>
public sealed record ReplayStep(string Payload, string Label);

/// <summary>Un escenario se reproduce con un solo comando: uno o varios POST firmados, en orden.</summary>
public sealed record ReplayScenario(string Name, string Description, string Expected, IReadOnlyList<ReplayStep> Steps);

public static class ReplayScenarios
{
    public static IReadOnlyList<ReplayScenario> All { get; } =
    [
        new("duplicado",
            "El mismo webhook 'delivered' dos veces (bytes idénticos), como cuando Meta reintenta.",
            "2 filas en WebhookInbox; 1 solo MessageEvent Delivered para el wamid.",
            [new("status-delivered", "delivered"), new("status-delivered", "delivered (repetido)")]),
        new("desorden",
            "read, delivered y sent llegan en orden inverso (timestamps en orden correcto).",
            "3 filas en WebhookInbox; MessageEvent Read, Delivered y Sent; EstadoEntrega queda en Read (no retrocede).",
            [new("status-read", "read"), new("status-delivered", "delivered"), new("status-sent", "sent")]),
        new("lote",
            "Un solo POST con 2 entry: sent y delivered en la primera, read en la segunda.",
            "1 fila en WebhookInbox; 3 MessageEvent (Sent, Delivered, Read); EstadoEntrega = Read.",
            [new("lote", "lote sent+delivered+read")]),
        new("stop",
            "user_preferences: el contacto deja de aceptar marketing desde WhatsApp (category marketing_messages).",
            "1 fila Procesado sin MessageEvent; 1 PlatformPreference Stop (Origen Webhook) para el contacto.",
            [new("user-preferences-stop", "user_preferences stop")]),
        new("resume",
            "user_preferences: el contacto vuelve a aceptar marketing.",
            "1 fila Procesado sin MessageEvent; 1 PlatformPreference Resume; no toca ConsentRecord (nunca levanta una baja de la empresa).",
            [new("user-preferences-resume", "user_preferences resume")]),
        new("baja-palabra",
            "Mensaje entrante de texto 'BAJA' del contacto (palabra clave).",
            "1 fila Procesado; 1 ConsentRecord OptOut (Fuente PalabraClave, WamidOrigen = id del mensaje).",
            [new("message-text-baja", "mensaje BAJA")]),
        new("baja-boton",
            "Toque del botón de respuesta rápida 'No recibir más' (payload BAJA_PROMOCIONES) de una plantilla.",
            "1 fila Procesado; 1 ConsentRecord OptOut (Fuente Boton).",
            [new("message-button-baja", "botón No recibir más")]),
        new("plantilla-paused",
            "message_template_status_update con event PAUSED (FIRST_PAUSE).",
            "1 fila Procesado; 1 Alert PlantillaPausada y pausa global. Levantar con: dotnet run --project src\\backend\\WaBulk.Worker -- reanudar",
            [new("template-paused", "plantilla PAUSED")]),
    ];

    public static ReplayScenario? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

public static class PayloadTemplates
{
    /// <summary>Lee la plantilla embebida y sustituye los marcadores. Devuelve los bytes UTF-8 que se firman y envían.</summary>
    public static byte[] Render(string payload, ReplayContext ctx)
    {
        var assembly = typeof(PayloadTemplates).Assembly;
        using var stream = assembly.GetManifestResourceStream($"payloads.{payload}.json")
            ?? throw new InvalidOperationException($"No existe la plantilla payloads/{payload}.json.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd()
            .Replace("{{WABA_ID}}", ctx.WabaId, StringComparison.Ordinal)
            .Replace("{{PHONE_NUMBER_ID}}", ctx.PhoneNumberId, StringComparison.Ordinal)
            .Replace("{{DISPLAY_PHONE}}", ctx.DisplayPhoneNumber, StringComparison.Ordinal)
            .Replace("{{WA_ID}}", ctx.WaId, StringComparison.Ordinal)
            .Replace("{{WAMID}}", ctx.Wamid, StringComparison.Ordinal)
            .Replace("{{IN_WAMID}}", ctx.InboundWamid, StringComparison.Ordinal)
            .Replace("{{ATTEMPT_ID}}", ctx.AttemptId.ToString("D"), StringComparison.Ordinal)
            .Replace("{{TS}}", Ts(ctx, 0), StringComparison.Ordinal)
            .Replace("{{TS1}}", Ts(ctx, 1), StringComparison.Ordinal)
            .Replace("{{TS2}}", Ts(ctx, 2), StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text);
    }

    public static IEnumerable<string> Names() =>
        typeof(PayloadTemplates).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("payloads.", StringComparison.Ordinal))
            .Select(n => n["payloads.".Length..^".json".Length]);

    private static string Ts(ReplayContext ctx, int offset) =>
        (ctx.BaseTimestamp + offset).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Firma X-Hub-Signature-256 igual que Meta: "sha256=" + HMAC-SHA256 en hex (minúsculas) del cuerpo con el App Secret.
/// Implementación propia (no la del receptor) para que la herramienta compruebe al receptor de forma independiente.
/// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/create-webhook-endpoint
/// </summary>
public static class ReplaySigner
{
    public const string HeaderName = "X-Hub-Signature-256";

    public static string Sign(byte[] body, string appSecret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body));

    /// <summary>Firma válida en formato pero calculada con otra clave: el receptor debe responder 401.</summary>
    public static string SignWrong(byte[] body, string appSecret) => Sign(body, appSecret + "-incorrecto");
}

public sealed record ReplayResult(ReplayStep Step, int Bytes, HttpStatusCode Status);

public sealed class ReplaySender(HttpClient http, Uri url, string appSecret, bool wrongSignature, TimeSpan pause)
{
    public async Task<IReadOnlyList<ReplayResult>> RunAsync(ReplayScenario scenario, ReplayContext ctx, CancellationToken ct = default)
    {
        var results = new List<ReplayResult>();
        foreach (var step in scenario.Steps)
        {
            if (results.Count > 0 && pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, ct);
            }

            var body = PayloadTemplates.Render(step.Payload, ctx);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add(ReplaySigner.HeaderName,
                wrongSignature ? ReplaySigner.SignWrong(body, appSecret) : ReplaySigner.Sign(body, appSecret));

            using var response = await http.SendAsync(request, ct);
            results.Add(new ReplayResult(step, body.Length, response.StatusCode));
        }

        return results;
    }

    /// <summary>Respuesta esperada del receptor: 200 con firma válida, 401 con firma incorrecta.</summary>
    public HttpStatusCode ExpectedStatus => wrongSignature ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
}
