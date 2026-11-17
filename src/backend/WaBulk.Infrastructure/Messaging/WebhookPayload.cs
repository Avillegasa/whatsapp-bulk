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
    [property: JsonPropertyName("statuses")] IReadOnlyList<WebhookStatus>? Statuses);

/// <summary>Un elemento de statuses[]. timestamp viene en segundos Unix como texto.</summary>
internal sealed record WebhookStatus(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("recipient_id")] string? RecipientId,
    [property: JsonPropertyName("biz_opaque_callback_data")] string? BizOpaqueCallbackData,
    [property: JsonPropertyName("pricing")] JsonElement? Pricing,
    [property: JsonPropertyName("errors")] JsonElement? Errors);
