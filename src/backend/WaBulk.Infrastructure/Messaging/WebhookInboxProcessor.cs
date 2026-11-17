using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>
/// Procesa WebhookInbox fuera de la petición (regla 6). Por cada fila: separa entry/changes/statuses,
/// correlaciona cada estado con su SendAttempt (AttemptId de biz_opaque_callback_data, si no por wamid),
/// registra MessageEvent una sola vez por (wamid, estado) y hace avanzar SendAttempt.EstadoEntrega
/// sin retroceder nunca (regla 7). Bajas (F0.10): mensajes entrantes con palabra clave o botón de baja →
/// ConsentRecord OptOut (capa empresa); user_preferences y estados failed con 131050 → PlatformPreference (capa
/// plataforma). Todo lo de una fila se guarda en un solo SaveChanges.
/// Referencia: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status
/// </summary>
public sealed partial class WebhookInboxProcessor(
    WaBulkDbContext db,
    IOptions<InboxProcessorOptions> options,
    IOptions<OptOutOptions> optOut,
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
                            outcomes.Add(await ApplyPlatformStopFromErrorAsync(status, cancellationToken));
                            outcomes.Add(await ApplyStatusAsync(status, seen, cancellationToken));
                        }

                        foreach (var message in change.Value?.Messages ?? [])
                        {
                            outcomes.Add(await ApplyInboundMessageAsync(message, change.Value?.Contacts, cancellationToken));
                        }

                        break;
                    case "user_preferences":
                        foreach (var preference in change.Value?.UserPreferences ?? [])
                        {
                            outcomes.Add(await ApplyUserPreferenceAsync(preference, cancellationToken));
                        }

                        break;
                    default:
                        outcomes.Add((Outcome.Ignored, null)); // account_update, plantillas, calidad…: F0.12 / Fase 1.
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
    /// Mensaje entrante: si es una baja (palabra clave o botón de baja), ConsentRecord OptOut. Cualquier otro
    /// mensaje se ignora por ahora (la interacción para la regla de fatiga llega en la Fase 1).
    /// </summary>
    private async Task<(Outcome Outcome, string? Note)> ApplyInboundMessageAsync(
        WebhookMessage message, IReadOnlyList<WebhookContact>? contacts, CancellationToken cancellationToken)
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
            return (Outcome.Ignored, null);
        }

        var phone = ConsentService.ToE164(message.From ?? (contacts is { Count: 1 } ? contacts[0].WaId : null));
        if (phone is null)
        {
            // Sin teléfono (usuario con nombre de usuario): no se puede aplicar por PhoneE164; lo resuelve una persona.
            return (Outcome.Review, $"baja sin teléfono (user_id {message.FromUserId}, mensaje {message.Id})");
        }

        if (string.IsNullOrEmpty(message.Id))
        {
            return (Outcome.Review, $"baja de {phone} sin id de mensaje");
        }

        if (db.ConsentRecords.Local.Any(c => c.SourceWamid == message.Id)
            || await db.ConsentRecords.AnyAsync(c => c.SourceWamid == message.Id, cancellationToken))
        {
            return (Outcome.Duplicate, null);
        }

        db.ConsentRecords.Add(new ConsentRecord
        {
            PhoneE164 = phone,
            Status = ConsentStatus.OptOut,
            Source = match.Source,
            Evidence = TruncateTo(match.Evidence, 1000),
            SourceWamid = message.Id,
            RecordedUtc = ParseUnixSeconds(message.Timestamp) ?? Now(),
            RecordedBy = "webhook",
        });
        LogOptOut(logger, phone, match.Source);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Preferencia de plataforma {Value} para {Phone} ({Origin})")]
    private static partial void LogPlatformPreference(ILogger logger, string phone, PlatformPreferenceValue value, PlatformPreferenceOrigin origin);

    [LoggerMessage(Level = LogLevel.Information, Message = "Estado de entrega no manejado: {Status}")]
    private static partial void LogUnknownStatus(ILogger logger, string? status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Intento {AttemptId} SubmissionUnknown resuelto como Accepted por webhook {Status}")]
    private static partial void LogUnknownResolved(ILogger logger, Guid attemptId, string? status);
}
