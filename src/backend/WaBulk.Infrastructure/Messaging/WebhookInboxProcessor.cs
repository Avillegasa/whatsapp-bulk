using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Alerts;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Messaging;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Contacts;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Infrastructure.Templates;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>
/// Procesa WebhookInbox fuera de la petición (regla 6). Por cada fila: separa entry/changes/statuses,
/// correlaciona cada estado con su SendAttempt (AttemptId de biz_opaque_callback_data, si no por wamid),
/// registra MessageEvent una sola vez por (wamid, estado) y hace avanzar SendAttempt.EstadoEntrega
/// sin retroceder nunca (regla 7). Bajas (F0.10): mensajes entrantes con palabra clave o botón de baja →
/// ConsentRecord OptOut (capa empresa); user_preferences y estados failed con 131050 → PlatformPreference (capa
/// plataforma). F1.4: todo mensaje entrante se guarda en InboundMessage con su Contact (mapeo BSUID ↔ teléfono) y la
/// respuesta a la oferta de promociones (D-031) se registra: «Sí» → ConsentRecord OptIn; «Ahora no» → solo la oferta.
/// F1.5: los webhooks de plantillas (estado, calidad, categoría) actualizan la tabla Template.
/// Todo lo de una fila se guarda en un solo SaveChanges, salvo un Contact nuevo, que se guarda antes para tener Id.
/// Referencia: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status
/// </summary>
public sealed partial class WebhookInboxProcessor(
    WaBulkDbContext db,
    IOptions<InboxProcessorOptions> options,
    IOptions<OptOutOptions> optOut,
    AlertService alerts,
    ContactDirectory contacts,
    TimeProvider clock,
    ILogger<WebhookInboxProcessor> logger)
{
    private const int MaxErrorLength = 4000;
    private static readonly JsonSerializerOptions JsonOptions = new();

    private enum Outcome { Applied, Duplicate, Ignored, Pending, Review }

    /// <summary>Procesa una vuelta. Devuelve cuántas filas tomó.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var now = Now();
        var ids = await db.WebhookInbox.AsNoTracking()
            .Where(x => x.Status == WebhookInboxStatus.Pendiente
                || (x.Status == WebhookInboxStatus.EnEspera && x.NextAttemptUtc <= now))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .Take(options.Value.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            await ProcessRowAsync(id, cancellationToken);
        }

        return ids.Count;
    }

    private async Task ProcessRowAsync(long id, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var row = await db.WebhookInbox.SingleAsync(x => x.Id == id, cancellationToken);
        row.Attempts++;

        WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(row.Body, JsonOptions);
        }
        catch (JsonException ex)
        {
            payload = null;
            LogInvalidJson(logger, row.Id, ex.Message);
        }

        if (payload?.Entry is null)
        {
            SetReview(row, "Cuerpo sin JSON válido o sin entry[]");
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var seen = new HashSet<(string Wamid, DeliveryStatus Status)>();
        var pending = new List<string>();
        var review = new List<string>();
        int applied = 0, duplicates = 0, ignored = 0;

        // Separación del lote: un POST puede traer varias entry, changes y statuses (hasta 1000 actualizaciones).
        foreach (var entry in payload.Entry)
        {
            foreach (var change in entry.Changes ?? [])
            {
                var outcomes = new List<(Outcome Outcome, string? Note)>();
                switch (change.Field)
                {
                    case "messages":
                        foreach (var status in change.Value?.Statuses ?? [])
                        {
                            outcomes.Add(await ApplyBillingAlertFromErrorAsync(status, cancellationToken));
                            outcomes.Add(await ApplyPlatformStopFromErrorAsync(status, cancellationToken));
                            outcomes.Add(await ApplyStatusAsync(status, seen, cancellationToken));
                        }

                        foreach (var message in change.Value?.Messages ?? [])
                        {
                            outcomes.Add(await ApplyInboundMessageAsync(row.Id, message, change.Value?.Contacts, cancellationToken));
                        }

                        break;
                    case "user_preferences":
                        foreach (var preference in change.Value?.UserPreferences ?? [])
                        {
                            outcomes.Add(await ApplyUserPreferenceAsync(preference, cancellationToken));
                        }

                        break;
                    case "message_template_status_update":
                        outcomes.Add(await ApplyTemplateStatusAsync(change.Value, EntryTime(entry), cancellationToken));
                        break;
                    case "message_template_quality_update":
                        outcomes.Add(await ApplyTemplateQualityAsync(change.Value, EntryTime(entry), cancellationToken));
                        break;
                    case "template_category_update":
                        outcomes.Add(await ApplyTemplateCategoryAsync(change.Value, EntryTime(entry), cancellationToken));
                        break;
                    default:
                        outcomes.Add((Outcome.Ignored, null)); // account_update, calidad del número…: F1.7.
                        break;
                }

                foreach (var (outcome, note) in outcomes)
                {
                    switch (outcome)
                    {
                        case Outcome.Applied: applied++; break;
                        case Outcome.Duplicate: duplicates++; break;
                        case Outcome.Ignored: ignored++; break;
                        case Outcome.Pending: pending.Add(note!); break;
                        case Outcome.Review: review.Add(note!); break;
                    }
                }
            }
        }

        if (review.Count > 0)
        {
            SetReview(row, string.Join("; ", review));
        }
        else if (pending.Count > 0)
        {
            SetWaitingOrReview(row, $"Sin correlación: {string.Join(", ", pending)}");
        }
        else
        {
            row.Status = WebhookInboxStatus.Procesado;
            row.NextAttemptUtc = null;
            row.Error = null;
        }

        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            // Otro procesador guardó lo mismo (índice único Wamid+Estado) u otro conflicto: se reintenta la fila;
            // al reintentar, lo ya guardado se detecta como duplicado.
            LogSaveConflict(logger, row.Id, ex.InnerException?.Message ?? ex.Message);
            db.ChangeTracker.Clear();
            var retry = await db.WebhookInbox.SingleAsync(x => x.Id == id, CancellationToken.None);
            retry.Attempts = row.Attempts;
            SetWaitingOrReview(retry, $"Conflicto al guardar: {ex.InnerException?.Message ?? ex.Message}");
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        LogRowDone(logger, row.Id, row.Status, applied, duplicates, ignored, pending.Count);
    }

    /// <summary>
    /// Mensaje entrante (F1.4): se guarda en InboundMessage con su Contact (abre la ventana de 24 h). Además:
    /// respuesta a la oferta de promociones → OptIn o «Ahora no» (D-031); palabra clave o botón de baja →
    /// ConsentRecord OptOut. Si Meta no manda el teléfono (nombre de usuario), se usa el del contacto mapeado por BSUID.
    /// </summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyInboundMessageAsync(
        long inboxId, WebhookMessage message, IReadOnlyList<WebhookContact>? webhookContacts, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message.Id))
        {
            return (Outcome.Review, "mensaje entrante sin id");
        }

        var info = webhookContacts?.FirstOrDefault(c =>
                       (message.FromUserId is not null && c.UserId == message.FromUserId) || (message.From is not null && c.WaId == message.From))
                   ?? (webhookContacts is { Count: 1 } ? webhookContacts[0] : null);
        var phone = PhoneNumbers.FromWaId(message.From ?? info?.WaId);
        var userId = message.FromUserId ?? info?.UserId;
        if (phone is null && userId is null)
        {
            return (Outcome.Review, $"mensaje {message.Id} sin teléfono ni user_id");
        }

        // Idempotencia: el mismo mensaje (webhook repetido) se aplica una sola vez.
        if (db.InboundMessages.Local.Any(m => m.Wamid == message.Id)
            || await db.InboundMessages.AnyAsync(m => m.Wamid == message.Id, cancellationToken))
        {
            return (Outcome.Duplicate, null);
        }

        var contact = await contacts.ResolveAsync(phone, userId, info?.Profile?.Name, "webhook", cancellationToken);
        phone ??= contact.PhoneE164; // Mapeo BSUID → teléfono.
        var sentUtc = ParseUnixSeconds(message.Timestamp) ?? Now();
        var reply = message.Interactive?.ButtonReply;
        db.InboundMessages.Add(new InboundMessage
        {
            Wamid = message.Id,
            ContactId = contact.Id,
            PhoneE164 = phone,
            UserId = userId,
            Type = TruncateTo(message.Type ?? "desconocido", 30),
            Text = message.Text?.Body is { } body ? TruncateTo(body, 4000) : reply?.Title ?? message.Button?.Text,
            ButtonId = reply?.Id ?? message.Button?.Payload,
            ContextWamid = message.Context?.Id,
            SentUtc = sentUtc,
            ReceivedUtc = Now(),
            WebhookInboxId = inboxId,
        });

        if (message.Type == "interactive" && reply?.Id is OfferButtons.Si or OfferButtons.AhoraNo)
        {
            return await ApplyOfferReplyAsync(message, reply, contact, phone, sentUtc, cancellationToken);
        }

        return await ApplyOptOutAsync(message, contact, phone, sentUtc, cancellationToken);
    }

    /// <summary>
    /// Respuesta a la oferta (D-031). La oferta se busca por context.id (wamid de la oferta) y, si no, la última sin
    /// respuesta del contacto. «Sí» → OptIn con la evidencia de Meta y de la oferta, aunque antes hubiera una baja (el
    /// cliente lo pidió), salvo No contactar. «Ahora no» → solo se anota en la oferta; el consentimiento no cambia.
    /// </summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyOfferReplyAsync(
        WebhookMessage message, WebhookButtonReply reply, Contact contact, string? phone, DateTime sentUtc,
        CancellationToken cancellationToken)
    {
        var contextId = message.Context?.Id;
        var offer = contextId is null ? null
            : await db.ConsentOffers.FirstOrDefaultAsync(o => o.Wamid == contextId, cancellationToken);
        offer ??= await db.ConsentOffers
            .Where(o => o.ContactId == contact.Id && o.Response == null && o.Status != ConsentOfferStatus.Rechazada)
            .OrderByDescending(o => o.OfferedUtc).FirstOrDefaultAsync(cancellationToken);
        if (offer is null)
        {
            return (Outcome.Review, $"respuesta {reply.Id} del contacto {contact.Id} sin oferta registrada (mensaje {message.Id})");
        }

        if (offer.Status is ConsentOfferStatus.Enviando or ConsentOfferStatus.Incierta)
        {
            // El cliente respondió: Meta sí entregó la oferta.
            offer.Status = ConsentOfferStatus.Enviada;
            offer.Wamid ??= contextId;
        }

        var yes = reply.Id == OfferButtons.Si;
        offer.Response = yes ? ConsentOfferResponse.Si : ConsentOfferResponse.AhoraNo;
        offer.ResponseUtc = sentUtc;
        offer.ResponseWamid = message.Id;
        LogOfferReply(logger, offer.Id, offer.Response.Value);
        if (!yes)
        {
            return (Outcome.Applied, null);
        }

        if (phone is null)
        {
            return (Outcome.Review, $"«Sí» del contacto {contact.Id} sin teléfono (user_id {message.FromUserId})");
        }

        var current = await db.ConsentRecords.AsNoTracking()
            .Where(c => c.PhoneE164 == phone && c.Purpose == ConsentPurposes.Promociones)
            .OrderByDescending(c => c.RecordedUtc).ThenByDescending(c => c.Id)
            .Select(c => (ConsentStatus?)c.Status).FirstOrDefaultAsync(cancellationToken);
        if (current == ConsentStatus.NoContactar)
        {
            offer.Detail = TruncateTo($"«Sí» recibido pero el contacto está en No contactar: no se registró OptIn. {offer.Detail}", MaxErrorLength);
            return (Outcome.Applied, null);
        }

        if (await db.ConsentRecords.AnyAsync(c => c.SourceWamid == message.Id, cancellationToken))
        {
            return (Outcome.Duplicate, null);
        }

        db.ConsentRecords.Add(new ConsentRecord
        {
            ContactId = contact.Id,
            PhoneE164 = phone,
            Status = ConsentStatus.OptIn,
            Source = ConsentSource.Boton,
            Evidence = TruncateTo(
                $"boton id={reply.Id} titulo={reply.Title} | oferta {offer.Id} texto {offer.TextVersion} ofrecida por {offer.OfferedBy} " +
                $"el {offer.OfferedUtc:yyyy-MM-dd HH:mm}Z | wamid oferta {offer.Wamid}", 1000),
            SourceWamid = message.Id,
            RecordedUtc = sentUtc,
            RecordedBy = "webhook",
        });
        return (Outcome.Applied, null);
    }

    /// <summary>Palabra clave o botón de baja → ConsentRecord OptOut (F0.10). Cualquier otro mensaje solo queda guardado.</summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyOptOutAsync(
        WebhookMessage message, Contact contact, string? phone, DateTime sentUtc, CancellationToken cancellationToken)
    {
        var opts = optOut.Value;
        (ConsentSource Source, string Evidence)? optOutMatch = message.Type switch
        {
            "text" when OptOutDetector.IsKeyword(message.Text?.Body, opts.EffectiveKeywords)
                => (ConsentSource.PalabraClave, message.Text!.Body!),
            "button" when OptOutDetector.IsOptOutButton(
                    message.Button?.Payload, message.Button?.Text, opts.EffectiveButtonPayloads, opts.EffectiveButtonTexts)
                => (ConsentSource.Boton, $"payload={message.Button!.Payload} text={message.Button.Text}"),
            "interactive" when OptOutDetector.IsOptOutButton(
                    message.Interactive?.ButtonReply?.Id, message.Interactive?.ButtonReply?.Title,
                    opts.EffectiveButtonPayloads, opts.EffectiveButtonTexts)
                => (ConsentSource.Boton, $"id={message.Interactive!.ButtonReply!.Id} title={message.Interactive.ButtonReply.Title}"),
            _ => null,
        };
        if (optOutMatch is not { } match)
        {
            return (Outcome.Applied, null); // Mensaje común: queda guardado en InboundMessage.
        }

        if (phone is null)
        {
            // Solo BSUID y sin teléfono mapeado: no se puede aplicar por PhoneE164; lo resuelve una persona.
            return (Outcome.Review, $"baja sin teléfono (user_id {message.FromUserId}, mensaje {message.Id})");
        }

        if (db.ConsentRecords.Local.Any(c => c.SourceWamid == message.Id)
            || await db.ConsentRecords.AnyAsync(c => c.SourceWamid == message.Id, cancellationToken))
        {
            return (Outcome.Duplicate, null);
        }

        db.ConsentRecords.Add(new ConsentRecord
        {
            ContactId = contact.Id,
            PhoneE164 = phone,
            Status = ConsentStatus.OptOut,
            Source = match.Source,
            Evidence = TruncateTo(match.Evidence, 1000),
            SourceWamid = message.Id,
            RecordedUtc = sentUtc,
            RecordedBy = "webhook",
        });
        LogOptOut(logger, phone, match.Source);
        return (Outcome.Applied, null);
    }

    /// <summary>
    /// Estados del mensaje de oferta (no es un SendAttempt): se reconocen por biz_opaque_callback_data "oferta:{Id}" o por
    /// wamid. Resuelven una oferta Enviando/Incierta; un failed la deja Rechazada. Devuelve null si no es de una oferta.
    /// </summary>
    private async Task<(Outcome Outcome, string? Note)?> ApplyOfferStatusAsync(WebhookStatus status, CancellationToken cancellationToken)
    {
        ConsentOffer? offer = null;
        var callback = status.BizOpaqueCallbackData;
        if (callback?.StartsWith(ConsentOfferService.CallbackPrefix, StringComparison.Ordinal) == true)
        {
            if (!long.TryParse(callback.AsSpan(ConsentOfferService.CallbackPrefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var offerId)
                || (offer = await db.ConsentOffers.FindAsync([offerId], cancellationToken)) is null)
            {
                return (Outcome.Review, $"estado de una oferta que no existe ({callback})");
            }
        }

        offer ??= status.Id is null ? null : await db.ConsentOffers.FirstOrDefaultAsync(o => o.Wamid == status.Id, cancellationToken);
        if (offer is null)
        {
            return null;
        }

        offer.Wamid ??= status.Id;
        if (status.Status == "failed")
        {
            offer.Status = ConsentOfferStatus.Rechazada;
            offer.Detail = Truncate($"failed por webhook: {RawJson(status.Errors)}");
        }
        else if (offer.Status is ConsentOfferStatus.Enviando or ConsentOfferStatus.Incierta)
        {
            offer.Status = ConsentOfferStatus.Enviada;
        }

        return (Outcome.Applied, null);
    }

    /// <summary>
    /// message_template_status_update (F1.5): actualiza Template.Estado si el evento es un estado (APPROVED, REJECTED,
    /// PAUSED, DISABLED…) y el dato no es más viejo que el guardado; otros eventos (FLAGGED, REINSTATED…) solo quedan en
    /// UltimoEvento. Si la plantilla no está en la tabla (creada en WhatsApp Manager) se agrega. PAUSED además levanta la
    /// alerta y la pausa global (F0.12), aunque el webhook llegue desordenado.
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/message_template_status_update
    /// </summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyTemplateStatusAsync(
        WebhookChangeValue? value, DateTime eventUtc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value?.Event) || (value.MessageTemplateName is null && TemplateMetaId(value) is null))
        {
            return (Outcome.Review, "message_template_status_update sin event o sin plantilla");
        }

        if (value.Event == TemplateStatuses.Paused)
        {
            var key = $"{value.MessageTemplateName}:{value.MessageTemplateLanguage}";
            await alerts.RaiseAsync(AlertType.PlantillaPausada, key,
                $"Meta pausó la plantilla {key} ({value.OtherInfo?.Title ?? value.Reason})",
                value.OtherInfo?.Description, "Webhook", cancellationToken);
        }

        var template = await FindTemplateAsync(value, cancellationToken);
        var isStatus = TemplateStatuses.FromMeta.Contains(value.Event);
        if (template is null)
        {
            if (!isStatus || value.MessageTemplateName is null)
            {
                return (Outcome.Ignored, null);
            }

            template = new Template
            {
                MetaTemplateId = TemplateMetaId(value),
                Name = value.MessageTemplateName,
                Language = TemplateRules.NormalizeLanguage(value.MessageTemplateLanguage),
                Category = value.MessageTemplateCategory ?? "",
                Status = value.Event,
                StatusUtc = DateTime.MinValue,
                CreatedUtc = Now(),
                CreatedBy = TemplateService.MetaActor,
            };
            db.Templates.Add(template);
        }

        if (eventUtc < template.StatusUtc)
        {
            return (Outcome.Duplicate, null); // Más viejo que lo guardado (webhook desordenado o sincronización posterior).
        }

        if (template.LastEvent == value.Event && template.StatusUtc == eventUtc && (!isStatus || template.Status == value.Event))
        {
            return (Outcome.Duplicate, null); // El mismo webhook repetido.
        }

        var reason = TemplateReason(value);
        template.LastEvent = value.Event;
        template.MetaTemplateId ??= TemplateMetaId(value);
        if (value.MessageTemplateCategory is { Length: > 0 } category)
        {
            template.Category = category;
        }

        if (isStatus)
        {
            template.Status = value.Event;
            template.StatusUtc = eventUtc;
            template.StatusReason = reason;
        }
        else
        {
            template.StatusReason = Truncate($"Evento {value.Event}: {reason ?? "sin detalle"}. Sincroniza para ver el estado actual.");
        }

        LogTemplateEvent(logger, template.Name, template.Language, value.Event);
        return (Outcome.Applied, null);
    }

    /// <summary>message_template_quality_update (F1.5): GREEN, YELLOW, RED o UNKNOWN. Solo si es más nuevo que el guardado.</summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyTemplateQualityAsync(
        WebhookChangeValue? value, DateTime eventUtc, CancellationToken cancellationToken)
    {
        if (value?.NewQualityScore is not { Length: > 0 } score)
        {
            return (Outcome.Review, "message_template_quality_update sin new_quality_score");
        }

        var template = await FindTemplateAsync(value, cancellationToken);
        if (template is null)
        {
            LogTemplateUnknown(logger, value.MessageTemplateName, "calidad");
            return (Outcome.Ignored, null); // La sincronización la traerá con su calidad.
        }

        if (template.QualityUtc is { } last && eventUtc <= last)
        {
            return (Outcome.Duplicate, null);
        }

        template.Quality = score;
        template.QualityUtc = eventUtc;
        LogTemplateQuality(logger, template.Name, value.PreviousQualityScore, score);
        return (Outcome.Applied, null);
    }

    /// <summary>
    /// template_category_update (F1.5). Con correct_category: Meta anuncia que la recategoriza (new_category es la
    /// categoría actual) → se guarda la categoría futura y la fecha. Con previous_category: la recategorización ya ocurrió.
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/template_category_update
    /// </summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyTemplateCategoryAsync(
        WebhookChangeValue? value, DateTime eventUtc, CancellationToken cancellationToken)
    {
        if (value?.NewCategory is not { Length: > 0 } newCategory)
        {
            return (Outcome.Review, "template_category_update sin new_category");
        }

        var template = await FindTemplateAsync(value, cancellationToken);
        if (template is null)
        {
            LogTemplateUnknown(logger, value.MessageTemplateName, "categoría");
            return (Outcome.Ignored, null);
        }

        if (value.CorrectCategory is { Length: > 0 } future)
        {
            DateTime? when = value.CategoryUpdateTimestamp is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : null;
            if (template.CorrectCategory == future && template.CategoryChangeUtc == when)
            {
                return (Outcome.Duplicate, null);
            }

            template.CorrectCategory = future;
            template.CategoryChangeUtc = when;
            LogTemplateCategory(logger, template.Name, newCategory, future);
            return (Outcome.Applied, null);
        }

        if (template.Category == newCategory && template.CorrectCategory is null)
        {
            return (Outcome.Duplicate, null);
        }

        template.PreviousCategory = value.PreviousCategory ?? template.Category;
        template.Category = newCategory;
        template.CorrectCategory = null;
        template.CategoryChangeUtc = eventUtc;
        LogTemplateCategory(logger, template.Name, template.PreviousCategory, newCategory);
        return (Outcome.Applied, null);
    }

    /// <summary>Busca la plantilla por id de Meta y, si no, por nombre e idioma (el webhook trae "en-US"; la tabla, "en_US").</summary>
    private async Task<Template?> FindTemplateAsync(WebhookChangeValue value, CancellationToken cancellationToken)
    {
        var metaId = TemplateMetaId(value);
        var template = metaId is null ? null
            : db.Templates.Local.FirstOrDefault(t => t.MetaTemplateId == metaId)
              ?? await db.Templates.FirstOrDefaultAsync(t => t.MetaTemplateId == metaId, cancellationToken);
        if (template is not null || value.MessageTemplateName is null)
        {
            return template;
        }

        var name = value.MessageTemplateName;
        var language = TemplateRules.NormalizeLanguage(value.MessageTemplateLanguage);
        return db.Templates.Local.FirstOrDefault(t => t.Name == name && t.Language == language)
               ?? await db.Templates.FirstOrDefaultAsync(t => t.Name == name && t.Language == language, cancellationToken);
    }

    /// <summary>message_template_id viene como número en el JSON; se guarda como texto.</summary>
    private static string? TemplateMetaId(WebhookChangeValue value) => value.MessageTemplateId switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        { ValueKind: JsonValueKind.String } t when !string.IsNullOrWhiteSpace(t.GetString()) => t.GetString(),
        _ => null,
    };

    private static string? TemplateReason(WebhookChangeValue value)
    {
        var parts = new[]
        {
            value.Reason is null or "NONE" ? null : value.Reason,
            value.RejectionInfo?.Reason,
            value.RejectionInfo?.Recommendation is { } r ? $"Recomendación: {r}" : null,
            value.OtherInfo?.Title,
            value.OtherInfo?.Description,
        }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : TruncateTo(string.Join(" | ", parts), MaxErrorLength);
    }

    /// <summary>entry.time (segundos Unix); si no viene, la hora actual.</summary>
    private DateTime EntryTime(WebhookEntry entry) => entry.Time switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime,
        { ValueKind: JsonValueKind.String } t => ParseUnixSeconds(t.GetString()) ?? Now(),
        _ => Now(),
    };

    /// <summary>Estado failed con 131042 (método de pago) → alerta y pausa global (F0.12).</summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyBillingAlertFromErrorAsync(WebhookStatus status, CancellationToken cancellationToken)
    {
        if (status.Status != "failed" || status.Errors is not { ValueKind: JsonValueKind.Array } errors
            || !errors.EnumerateArray().Any(e => e.TryGetProperty("code", out var c) && c.TryGetInt32(out var code)
                                                 && EmergencyBrake.ForErrorCode(code) == AlertType.Facturacion))
        {
            return (Outcome.Ignored, null);
        }

        await alerts.RaiseAsync(AlertType.Facturacion, "global", $"Error 131042 en el estado del mensaje {Shorten(status.Id ?? "")}",
            errors.GetRawText(), "Webhook", cancellationToken);
        return (Outcome.Applied, null);
    }

    /// <summary>user_preferences stop/resume de marketing → PlatformPreference (nunca toca la capa empresa).</summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyUserPreferenceAsync(
        WebhookUserPreference preference, CancellationToken cancellationToken)
    {
        PlatformPreferenceValue? value = preference.Value switch
        {
            "stop" => PlatformPreferenceValue.Stop,
            "resume" => PlatformPreferenceValue.Resume,
            _ => null,
        };
        if (preference.Category != PlatformCategories.MarketingMessages || value is null)
        {
            return (Outcome.Ignored, null);
        }

        var phone = ConsentService.ToE164(preference.WaId);
        if (phone is null)
        {
            return (Outcome.Review, $"user_preferences {preference.Value} sin wa_id (user_id {preference.UserId})");
        }

        var timestamp = preference.Timestamp switch
        {
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime,
            { ValueKind: JsonValueKind.String } t => ParseUnixSeconds(t.GetString()),
            _ => null,
        } ?? Now();

        return await AddPlatformPreferenceAsync(phone, value.Value, PlatformPreferenceOrigin.Webhook, timestamp, cancellationToken);
    }

    /// <summary>Estado failed con error 131050: el contacto detuvo el marketing en WhatsApp.</summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyPlatformStopFromErrorAsync(
        WebhookStatus status, CancellationToken cancellationToken)
    {
        if (status.Status != "failed" || status.Errors is not { ValueKind: JsonValueKind.Array } errors
            || !errors.EnumerateArray().Any(e => e.TryGetProperty("code", out var c) && c.TryGetInt32(out var code)
                                                 && code == Dispatcher.PlatformStopErrorCode))
        {
            return (Outcome.Ignored, null);
        }

        var phone = ConsentService.ToE164(status.RecipientId);
        if (phone is null)
        {
            return (Outcome.Review, $"131050 sin recipient_id ({Shorten(status.Id ?? "")})");
        }

        return await AddPlatformPreferenceAsync(phone, PlatformPreferenceValue.Stop, PlatformPreferenceOrigin.Error131050,
            ParseUnixSeconds(status.Timestamp) ?? Now(), cancellationToken);
    }

    private async Task<(Outcome Outcome, string? Note)> AddPlatformPreferenceAsync(
        string phone, PlatformPreferenceValue value, PlatformPreferenceOrigin origin, DateTime recordedUtc,
        CancellationToken cancellationToken)
    {
        if (db.PlatformPreferences.Local.Any(p => p.PhoneE164 == phone && p.RecordedUtc == recordedUtc && p.Value == value)
            || await db.PlatformPreferences.AnyAsync(
                p => p.PhoneE164 == phone && p.Category == PlatformCategories.MarketingMessages
                     && p.RecordedUtc == recordedUtc && p.Value == value, cancellationToken))
        {
            return (Outcome.Duplicate, null);
        }

        db.PlatformPreferences.Add(new PlatformPreference
        {
            PhoneE164 = phone,
            Value = value,
            Origin = origin,
            RecordedUtc = recordedUtc,
        });
        LogPlatformPreference(logger, phone, value, origin);
        return (Outcome.Applied, null);
    }

    private async Task<(Outcome Outcome, string? Note)> ApplyStatusAsync(
        WebhookStatus status, HashSet<(string, DeliveryStatus)> seen, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(status.Id))
        {
            return (Outcome.Review, "estado sin id (wamid)");
        }

        if (await ApplyOfferStatusAsync(status, cancellationToken) is { } offerOutcome)
        {
            return offerOutcome;
        }

        var wamid = status.Id;
        DeliveryStatus? parsed = status.Status switch
        {
            "sent" => DeliveryStatus.Sent,
            "delivered" => DeliveryStatus.Delivered,
            "read" => DeliveryStatus.Read,
            "failed" => DeliveryStatus.Failed,
            _ => null, // "played" (notas de voz) u otros: no aplican a plantillas de promoción.
        };
        if (parsed is not { } deliveryStatus)
        {
            LogUnknownStatus(logger, status.Status);
            return (Outcome.Ignored, null);
        }

        // Correlación: primero por AttemptId (sirve aunque el wamid aún no esté guardado), luego por wamid.
        SendAttempt? attempt = null;
        if (Guid.TryParse(status.BizOpaqueCallbackData, out var attemptId))
        {
            attempt = await db.SendAttempts.FindAsync([attemptId], cancellationToken);
        }

        attempt ??= db.SendAttempts.Local.FirstOrDefault(a => a.Wamid == wamid)
            ?? await db.SendAttempts.SingleOrDefaultAsync(a => a.Wamid == wamid, cancellationToken);

        if (attempt is null)
        {
            return (Outcome.Pending, $"{Shorten(wamid)} {status.Status}");
        }

        if (attempt.Wamid is not null && attempt.Wamid != wamid)
        {
            return (Outcome.Review, $"el intento {attempt.AttemptId} tiene otro wamid que {Shorten(wamid)}");
        }

        if (attempt.Status == SendAttemptStatus.Rejected)
        {
            return (Outcome.Review, $"estado {status.Status} para el intento rechazado {attempt.AttemptId}");
        }

        if (attempt.Wamid is null)
        {
            // El webhook llegó antes de que el despachador guardara la respuesta, o el intento quedó sin respuesta.
            attempt.Wamid = wamid;
        }

        if (attempt.Status == SendAttemptStatus.SubmissionUnknown)
        {
            // Meta sí recibió el mensaje: el intento se resuelve sin reenviar (ARQUITECTURA, "Resultado de un intento").
            attempt.Status = SendAttemptStatus.Accepted;
            attempt.Detail = Truncate($"Resuelto por webhook {status.Status}. {attempt.Detail}");
            LogUnknownResolved(logger, attempt.AttemptId, status.Status);

            // El trabajo que quedó en NeedsReview por este intento pasa a Submitted (F0.9).
            var job = await db.DispatchJobs.FindAsync([attempt.DispatchJobId], cancellationToken);
            if (job is { DispatchStatus: DispatchStatus.NeedsReview })
            {
                job.DispatchStatus = DispatchStatus.Submitted;
            }
        }

        // Solo avanza: Sent < Delivered < Read < Failed (valores numéricos de DeliveryStatus).
        if (attempt.DeliveryStatus is null || deliveryStatus > attempt.DeliveryStatus)
        {
            attempt.DeliveryStatus = deliveryStatus;
        }

        // Idempotencia: un solo MessageEvent por (wamid, estado), dentro del lote y contra lo ya guardado.
        if (!seen.Add((wamid, deliveryStatus))
            || await db.MessageEvents.AnyAsync(e => e.Wamid == wamid && e.Status == deliveryStatus, cancellationToken))
        {
            return (Outcome.Duplicate, null);
        }

        db.MessageEvents.Add(new MessageEvent
        {
            Wamid = wamid,
            AttemptId = attempt.AttemptId,
            Status = deliveryStatus,
            MetaTimestampUtc = ParseUnixSeconds(status.Timestamp) ?? Now(),
            Pricing = RawJson(status.Pricing),
            Errors = RawJson(status.Errors),
            CreatedUtc = Now(),
        });
        return (Outcome.Applied, null);
    }

    private void SetWaitingOrReview(WebhookInbox row, string error)
    {
        var opts = options.Value;
        if (row.Attempts >= opts.MaxAttempts)
        {
            SetReview(row, $"{error} (tras {row.Attempts} intentos)");
            return;
        }

        var delay = Math.Min(opts.RetryDelaySeconds * Math.Pow(2, row.Attempts - 1), opts.MaxRetryDelaySeconds);
        row.Status = WebhookInboxStatus.EnEspera;
        row.NextAttemptUtc = Now().AddSeconds(delay);
        row.Error = Truncate(error);
    }

    private void SetReview(WebhookInbox row, string error)
    {
        row.Status = WebhookInboxStatus.Revision;
        row.NextAttemptUtc = null;
        row.Error = Truncate(error);
        LogReview(logger, row.Id, row.Error);
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static DateTime? ParseUnixSeconds(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    private static string? RawJson(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } e ? e.GetRawText() : null;

    private static string Shorten(string wamid) => wamid.Length <= 24 ? wamid : $"{wamid[..24]}…";

    private static string Truncate(string value) => TruncateTo(value, MaxErrorLength);

    private static string TruncateTo(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Inbox {InboxId} → {Status}: {Applied} eventos nuevos, {Duplicates} duplicados, {Ignored} ignorados, {Pending} sin correlación")]
    private static partial void LogRowDone(ILogger logger, long inboxId, WebhookInboxStatus status, int applied, int duplicates, int ignored, int pending);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox {InboxId} pasa a Revision: {Error}")]
    private static partial void LogReview(ILogger logger, long inboxId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox {InboxId}: JSON inválido ({Error})")]
    private static partial void LogInvalidJson(ILogger logger, long inboxId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox {InboxId}: conflicto al guardar, se reintenta ({Error})")]
    private static partial void LogSaveConflict(ILogger logger, long inboxId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Baja de empresa registrada para {Phone} ({Source})")]
    private static partial void LogOptOut(ILogger logger, string phone, ConsentSource source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Respuesta a la oferta {OfferId}: {Response}")]
    private static partial void LogOfferReply(ILogger logger, long offerId, ConsentOfferResponse response);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preferencia de plataforma {Value} para {Phone} ({Origin})")]
    private static partial void LogPlatformPreference(ILogger logger, string phone, PlatformPreferenceValue value, PlatformPreferenceOrigin origin);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plantilla {Name} ({Language}): evento {Event}")]
    private static partial void LogTemplateEvent(ILogger logger, string name, string language, string @event);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plantilla {Name}: calidad {Previous} → {New}")]
    private static partial void LogTemplateQuality(ILogger logger, string name, string? previous, string @new);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plantilla {Name}: categoría {Previous} → {New}")]
    private static partial void LogTemplateCategory(ILogger logger, string name, string? previous, string @new);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de {Kind} para la plantilla {Name}, que no está en la tabla: se ignora hasta sincronizar")]
    private static partial void LogTemplateUnknown(ILogger logger, string? name, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Estado de entrega no manejado: {Status}")]
    private static partial void LogUnknownStatus(ILogger logger, string? status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Intento {AttemptId} SubmissionUnknown resuelto como Accepted por webhook {Status}")]
    private static partial void LogUnknownResolved(ILogger logger, Guid attemptId, string? status);
}
