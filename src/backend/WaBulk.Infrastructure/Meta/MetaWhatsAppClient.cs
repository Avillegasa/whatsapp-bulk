using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Meta;

/// <summary>
/// Cliente tipado de la Cloud API para POST /{version}/{PHONE_NUMBER_ID}/messages.
/// Sin reintentos: cada llamada es exactamente un intento (regla 3). Cualquier falla sin respuesta
/// confiable se clasifica como SubmissionUnknown (regla 4).
/// Referencias (consultadas 2026-11-16):
/// - Envío y respuesta: https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/send-messages
/// - Esquema de respuesta: https://developers.facebook.com/documentation/business-messaging/whatsapp/reference/whatsapp-business-phone-number/message-api
/// - Formato de error: https://developers.facebook.com/documentation/business-messaging/whatsapp/support/error-codes
/// - biz_opaque_callback_data (string opcional, máx. 512, vuelve en webhooks sent/delivered/read):
///   https://developers.facebook.com/docs/whatsapp/cloud-api/reference/messages/
/// </summary>
public sealed partial class MetaWhatsAppClient(
    HttpClient httpClient,
    IOptionsMonitor<MetaOptions> options,
    ILogger<MetaWhatsAppClient> logger)
{
    private const int MaxDetailLength = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<MetaSendResult> SendTemplateAsync(
        string to,
        TemplateMessage template,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        var meta = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(meta.GraphApiVersion) || string.IsNullOrWhiteSpace(meta.PhoneNumberId)
            || string.IsNullOrWhiteSpace(meta.AccessToken))
        {
            throw new InvalidOperationException(
                "Configuración de Meta incompleta: se requieren Meta:GraphApiVersion, Meta:PhoneNumberId y Meta:AccessToken.");
        }

        var body = new SendTemplateRequest(
            To: to,
            Template: new TemplatePayload(template.Name, new LanguagePayload(template.LanguageCode)),
            BizOpaqueCallbackData: attemptId.ToString());

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(meta.BaseUrl, $"{meta.GraphApiVersion}/{meta.PhoneNumberId}/messages"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", meta.AccessToken);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        HttpResponseMessage response;
        string responseText;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
            responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            // Timeout, conexión cortada o cancelación: no sabemos si Meta recibió el mensaje.
            LogUnknown(logger, attemptId, ex.GetType().Name);
            return new MetaSendResult(SendAttemptStatus.SubmissionUnknown, null, null, null,
                Truncate($"{ex.GetType().Name}: {ex.Message}"));
        }

        using (response)
        {
            return Classify((int)response.StatusCode, response.IsSuccessStatusCode, responseText);
        }
    }

    private static MetaSendResult Classify(int httpStatus, bool isSuccess, string responseText)
    {
        if (isSuccess)
        {
            var ok = TryDeserialize<SendResponse>(responseText);
            var message = ok?.Messages?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(message?.Id))
            {
                return new MetaSendResult(SendAttemptStatus.Accepted, message.Id, httpStatus, null,
                    message.MessageStatus is null ? null : $"message_status={message.MessageStatus}");
            }

            // 2xx sin wamid: no podemos afirmar que se envió ni que no.
            return new MetaSendResult(SendAttemptStatus.SubmissionUnknown, null, httpStatus, null,
                Truncate($"Respuesta 2xx sin messages[0].id: {responseText}"));
        }

        var error = TryDeserialize<ErrorResponse>(responseText)?.Error;
        if (error?.Code is int code)
        {
            return new MetaSendResult(SendAttemptStatus.Rejected, null, httpStatus, code,
                Truncate($"{error.Message} | {error.ErrorData?.Details} | fbtrace_id={error.FbtraceId}"));
        }

        // 5xx sin cuerpo de error reconocible: resultado incierto. 4xx sin cuerpo: rechazo sin código.
        return httpStatus >= 500
            ? new MetaSendResult(SendAttemptStatus.SubmissionUnknown, null, httpStatus, null, Truncate(responseText))
            : new MetaSendResult(SendAttemptStatus.Rejected, null, httpStatus, null, Truncate(responseText));
    }

    private static T? TryDeserialize<T>(string text)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxDetailLength ? text : text[..MaxDetailLength];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intento {AttemptId} sin respuesta confiable de Meta ({Reason}) → SubmissionUnknown")]
    private static partial void LogUnknown(ILogger logger, Guid attemptId, string reason);

    // --- Payloads (snake_case) ---

    private sealed record SendTemplateRequest(string To, TemplatePayload Template, string BizOpaqueCallbackData)
    {
        public string MessagingProduct => "whatsapp";
        public string RecipientType => "individual";
        public string Type => "template";
    }

    private sealed record TemplatePayload(string Name, LanguagePayload Language);

    private sealed record LanguagePayload(string Code)
    {
        public string Policy => "deterministic";
    }

    private sealed record SendResponse(IReadOnlyList<SendResponseMessage>? Messages);

    private sealed record SendResponseMessage(string? Id, string? MessageStatus);

    private sealed record ErrorResponse(MetaError? Error);

    private sealed record MetaError(string? Message, string? Type, int? Code, MetaErrorData? ErrorData, string? FbtraceId);

    private sealed record MetaErrorData(string? Details);
}
