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

    public Task<MetaSendResult> SendTemplateAsync(
        string to,
        TemplateMessage template,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);

        // components (F1.5): solo el cuerpo lleva parámetros. Los botones de respuesta rápida no se envían: sin payload
        // propio, Meta devuelve en el webhook button.payload = texto del botón ("No recibir más"), que es lo que reconoce
        // OptOutDetector (Bajas:ButtonTexts).
        // https://developers.facebook.com/documentation/business-messaging/whatsapp/templates/overview (named/positional)
        // https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/button
        IReadOnlyList<ComponentPayload>? components = template.BodyParameters is { Count: > 0 } values
            ? [new ComponentPayload("body", [.. values.Select(v => new ParameterPayload(
                v.Value, template.NamedParameters ? v.Name : null))])]
            : null;

        var body = new SendTemplateRequest(
            To: to,
            Template: new TemplatePayload(template.Name, new LanguagePayload(template.LanguageCode), components),
            BizOpaqueCallbackData: attemptId.ToString());
        return PostAsync(body, attemptId.ToString(), cancellationToken);
    }

    /// <summary>
    /// Mensaje interactivo con botones de respuesta (no es plantilla: solo dentro de la ventana de 24 h).
    /// Hasta 3 botones, título ≤ 20, id ≤ 256, cuerpo ≤ 1024. Destinatario: teléfono en "to" o, si no hay, el BSUID en
    /// "recipient". Un solo POST, sin reintento (regla 3).
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/interactive-reply-buttons-messages
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/send-messages (recipient = BSUID)
    /// </summary>
    public Task<MetaSendResult> SendReplyButtonsAsync(
        string? phoneE164,
        string? userId,
        string bodyText,
        IReadOnlyList<(string Id, string Title)> buttons,
        string callbackData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        if (string.IsNullOrWhiteSpace(phoneE164) && string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("Se necesita teléfono o BSUID.");
        }

        var body = new SendInteractiveRequest(
            To: string.IsNullOrWhiteSpace(phoneE164) ? null : phoneE164.TrimStart('+'),
            Recipient: string.IsNullOrWhiteSpace(phoneE164) ? userId : null,
            Interactive: new InteractivePayload(
                new TextPayload(bodyText),
                new ActionPayload([.. buttons.Select(b => new ButtonPayload(new ReplyPayload(b.Id, b.Title)))])),
            BizOpaqueCallbackData: callbackData);
        return PostAsync(body, callbackData, cancellationToken);
    }

    /// <summary>
    /// Límite de mensajería del número (F1.7): GET /{PHONE_NUMBER_ID}?fields=whatsapp_business_manager_messaging_limit →
    /// "TIER_250"… Es una lectura: no tiene riesgo de duplicar envíos, pero tampoco se reintenta (lo hace la próxima vuelta).
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/messaging-limits
    /// </summary>
    public async Task<string?> GetMessagingLimitAsync(CancellationToken cancellationToken)
    {
        var meta = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(meta.GraphApiVersion) || string.IsNullOrWhiteSpace(meta.PhoneNumberId)
            || string.IsNullOrWhiteSpace(meta.AccessToken))
        {
            throw new MetaApiException("Configuración de Meta incompleta (GraphApiVersion, PhoneNumberId, AccessToken).");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(meta.BaseUrl, $"{meta.GraphApiVersion}/{meta.PhoneNumberId}?fields=whatsapp_business_manager_messaging_limit"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", meta.AccessToken);
        string text;
        int status;
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new MetaApiException($"Sin respuesta de Meta al leer el límite ({ex.GetType().Name}).");
        }

        if (status is < 200 or >= 300)
        {
            var error = TryDeserialize<ErrorResponse>(text)?.Error;
            throw new MetaApiException($"Meta respondió {status} al leer el límite: {error?.Message ?? Truncate(text)}", error?.Code);
        }

        return TryDeserialize<LimitResponse>(text)?.WhatsappBusinessManagerMessagingLimit;
    }

    private sealed record LimitResponse(string? WhatsappBusinessManagerMessagingLimit);

    private async Task<MetaSendResult> PostAsync<TBody>(TBody body, string logId, CancellationToken cancellationToken)
    {
        var meta = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(meta.GraphApiVersion) || string.IsNullOrWhiteSpace(meta.PhoneNumberId)
            || string.IsNullOrWhiteSpace(meta.AccessToken))
        {
            throw new InvalidOperationException(
                "Configuración de Meta incompleta: se requieren Meta:GraphApiVersion, Meta:PhoneNumberId y Meta:AccessToken.");
        }

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
            LogUnknown(logger, logId, ex.GetType().Name);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Envío {Id} sin respuesta confiable de Meta ({Reason}) → SubmissionUnknown")]
    private static partial void LogUnknown(ILogger logger, string id, string reason);

    // --- Payloads (snake_case) ---

    private sealed record SendTemplateRequest(string To, TemplatePayload Template, string BizOpaqueCallbackData)
    {
        public string MessagingProduct => "whatsapp";
        public string RecipientType => "individual";
        public string Type => "template";
    }

    private sealed record TemplatePayload(string Name, LanguagePayload Language, IReadOnlyList<ComponentPayload>? Components);

    private sealed record ComponentPayload(string Type, IReadOnlyList<ParameterPayload> Parameters);

    /// <summary>{"type":"text","parameter_name":"nombre","text":"Andrés"}; parameter_name se omite en POSITIONAL.</summary>
    private sealed record ParameterPayload(string Text, string? ParameterName)
    {
        public string Type => "text";
    }

    private sealed record SendInteractiveRequest(string? To, string? Recipient, InteractivePayload Interactive, string BizOpaqueCallbackData)
    {
        public string MessagingProduct => "whatsapp";
        public string RecipientType => "individual";
        public string Type => "interactive";
    }

    private sealed record InteractivePayload(TextPayload Body, ActionPayload Action)
    {
        public string Type => "button";
    }

    private sealed record TextPayload(string Text);

    private sealed record ActionPayload(IReadOnlyList<ButtonPayload> Buttons);

    private sealed record ButtonPayload(ReplyPayload Reply)
    {
        public string Type => "reply";
    }

    private sealed record ReplyPayload(string Id, string Title);

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
