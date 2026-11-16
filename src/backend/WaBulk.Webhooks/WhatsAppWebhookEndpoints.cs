using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Webhooks.Security;

namespace WaBulk.Webhooks;

/// <summary>
/// Receptor de webhooks de WhatsApp (único componente público). Solo valida, guarda en WebhookInbox y responde;
/// el procesamiento ocurre fuera de la petición (F0.7). Regla 6 de CLAUDE.md.
/// Referencia: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/create-webhook-endpoint
/// </summary>
public static partial class WhatsAppWebhookEndpoints
{
    public const string Route = "/webhooks/whatsapp";

    public static IEndpointRouteBuilder MapWhatsAppWebhook(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, Verify);
        app.MapPost(Route, ReceiveAsync);
        return app;
    }

    /// <summary>Handshake: Meta envía hub.mode=subscribe, hub.verify_token y hub.challenge; se responde 200 con el challenge.</summary>
    private static IResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        IOptionsSnapshot<MetaOptions> meta,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(WhatsAppWebhookEndpoints));
        var expected = meta.Value.VerifyToken;
        if (string.IsNullOrEmpty(expected))
        {
            LogMissingSecret(logger, "Meta:VerifyToken");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (mode == "subscribe" && !string.IsNullOrEmpty(challenge) && ConstantTimeEquals(verifyToken, expected))
        {
            LogVerified(logger);
            return Results.Text(challenge, "text/plain");
        }

        LogVerifyRejected(logger);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ReceiveAsync(
        HttpContext context,
        WaBulkDbContext db,
        IOptionsSnapshot<MetaOptions> meta,
        IOptions<WebhookOptions> webhookOptions,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(WhatsAppWebhookEndpoints));
        var appSecret = meta.Value.AppSecret;
        if (string.IsNullOrEmpty(appSecret))
        {
            // 503: Meta reintenta; no se pierde el evento mientras se corrige la configuración.
            LogMissingSecret(logger, "Meta:AppSecret");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var body = await ReadBodyAsync(context, webhookOptions.Value.MaxBodyBytes);
        if (body is null)
        {
            LogTooLarge(logger, webhookOptions.Value.MaxBodyBytes);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!WebhookSignature.IsValid(body, context.Request.Headers[WebhookSignature.HeaderName], appSecret))
        {
            LogInvalidSignature(logger, body.Length);
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var entry = new WebhookInbox
        {
            ReceivedUtc = clock.GetUtcNow().UtcDateTime,
            Body = Encoding.UTF8.GetString(body),
            Status = WebhookInboxStatus.Pendiente,
        };
        db.WebhookInbox.Add(entry);
        await db.SaveChangesAsync(CancellationToken.None); // Guardado ANTES de responder 200.

        LogStored(logger, entry.Id, body.Length);
        return Results.Ok();
    }

    /// <summary>Lee el cuerpo crudo con límite. Devuelve null si lo supera.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, long maxBytes)
    {
        if (context.Request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null; // Límite de Kestrel (MaxRequestBodySize).
        }

        return buffer.ToArray();
    }

    /// <summary>Compara en tiempo constante; los hashes igualan la longitud para no filtrarla.</summary>
    private static bool ConstantTimeEquals(string? received, string expected) =>
        received is not null && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(received)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    [LoggerMessage(Level = LogLevel.Error, Message = "Falta {Setting}: el webhook responde 503 hasta configurarlo")]
    private static partial void LogMissingSecret(ILogger logger, string setting);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handshake de webhook verificado")]
    private static partial void LogVerified(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handshake de webhook rechazado (modo o verify token no coinciden)")]
    private static partial void LogVerifyRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook rechazado: cuerpo mayor a {MaxBytes} bytes")]
    private static partial void LogTooLarge(ILogger logger, long maxBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook rechazado: firma inválida o ausente ({Bytes} bytes)")]
    private static partial void LogInvalidSignature(ILogger logger, int bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook guardado en WebhookInbox Id={InboxId} ({Bytes} bytes)")]
    private static partial void LogStored(ILogger logger, long inboxId, int bytes);
}
