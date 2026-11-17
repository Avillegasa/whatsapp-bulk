using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaBulk.Infrastructure.Messaging;

// Forma del webhook de WhatsApp: object → entry[] → changes[] (field, value) → value.statuses[].
// Solo se modela lo que usa el procesador; el resto del cuerpo queda en WebhookInbox.Cuerpo.
// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status

internal sealed record WebhookPayload(
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("entry")] IReadOnlyList<WebhookEntry>? Entry);

internal sealed record WebhookEntry(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("changes")] IReadOnlyList<WebhookChange>? Changes);

internal sealed record WebhookChange(
    [property: JsonPropertyName("field")] string? Field,
    [property: JsonPropertyName("value")] WebhookChangeValue? Value);

internal sealed record WebhookChangeValue(
    [property: JsonPropertyName("statuses")] IReadOnlyList<WebhookStatus>? Statuses,
    [property: JsonPropertyName("contacts")] IReadOnlyList<WebhookContact>? Contacts,
    [property: JsonPropertyName("messages")] IReadOnlyList<WebhookMessage>? Messages,
    [property: JsonPropertyName("user_preferences")] IReadOnlyList<WebhookUserPreference>? UserPreferences,
    // message_template_status_update (F0.12):
    // https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/message_template_status_update
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("message_template_id")] JsonElement? MessageTemplateId,
    [property: JsonPropertyName("message_template_name")] string? MessageTemplateName,
    [property: JsonPropertyName("message_template_language")] string? MessageTemplateLanguage,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("other_info")] WebhookOtherInfo? OtherInfo);

internal sealed record WebhookOtherInfo(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description);

// Mensajes entrantes (F0.10). from y wa_id se omiten si el usuario adoptó un nombre de usuario; user_id (BSUID) siempre viene.
// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/text
// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/button

internal sealed record WebhookContact(
    [property: JsonPropertyName("wa_id")] string? WaId,
    [property: JsonPropertyName("user_id")] string? UserId);

internal sealed record WebhookMessage(
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("from_user_id")] string? FromUserId,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] WebhookText? Text,
    [property: JsonPropertyName("button")] WebhookButton? Button,
    [property: JsonPropertyName("interactive")] WebhookInteractive? Interactive);

internal sealed record WebhookText([property: JsonPropertyName("body")] string? Body);

/// <summary>Botón de respuesta rápida de una plantilla.</summary>
internal sealed record WebhookButton(
    [property: JsonPropertyName("payload")] string? Payload,
    [property: JsonPropertyName("text")] string? Text);

internal sealed record WebhookInteractive(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("button_reply")] WebhookButtonReply? ButtonReply);

internal sealed record WebhookButtonReply(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>
/// user_preferences: value stop/resume, category marketing_messages; timestamp es entero (segundos Unix).
/// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/user_preferences
/// </summary>
internal sealed record WebhookUserPreference(
    [property: JsonPropertyName("wa_id")] string? WaId,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("timestamp")] JsonElement? Timestamp);

/// <summary>Un elemento de statuses[]. timestamp viene en segundos Unix como texto.</summary>
internal sealed record WebhookStatus(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("recipient_id")] string? RecipientId,
    [property: JsonPropertyName("biz_opaque_callback_data")] string? BizOpaqueCallbackData,
    [property: JsonPropertyName("pricing")] JsonElement? Pricing,
    [property: JsonPropertyName("errors")] JsonElement? Errors);
