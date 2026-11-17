using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>
/// Procesa WebhookInbox fuera de la petición (regla 6). Por cada fila: separa entry/changes/statuses,
/// correlaciona cada estado con su SendAttempt (AttemptId de biz_opaque_callback_data, si no por wamid),
/// registra MessageEvent una sola vez por (wamid, estado) y hace avanzar SendAttempt.EstadoEntrega
/// sin retroceder nunca (regla 7). Todo lo de una fila se guarda en un solo SaveChanges.
/// Referencia: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status
/// </summary>
public sealed partial class WebhookInboxProcessor(
    WaBulkDbContext db,
    IOptions<InboxProcessorOptions> options,
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
                var statuses = change.Field == "messages" ? change.Value?.Statuses : null;
                if (statuses is null)
                {
                    ignored++; // Otros campos (mensajes entrantes, account_update, plantillas…): F0.10 / Fase 1.
                    continue;
                }

                foreach (var status in statuses)
                {
                    var (outcome, note) = await ApplyStatusAsync(status, seen, cancellationToken);
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

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Inbox {InboxId} → {Status}: {Applied} eventos nuevos, {Duplicates} duplicados, {Ignored} ignorados, {Pending} sin correlación")]
    private static partial void LogRowDone(ILogger logger, long inboxId, WebhookInboxStatus status, int applied, int duplicates, int ignored, int pending);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox {InboxId} pasa a Revision: {Error}")]
    private static partial void LogReview(ILogger logger, long inboxId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox {InboxId}: JSON inválido ({Error})")]
    private static partial void LogInvalidJson(ILogger logger, long inboxId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox {InboxId}: conflicto al guardar, se reintenta ({Error})")]
    private static partial void LogSaveConflict(ILogger logger, long inboxId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Estado de entrega no manejado: {Status}")]
    private static partial void LogUnknownStatus(ILogger logger, string? status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Intento {AttemptId} SubmissionUnknown resuelto como Accepted por webhook {Status}")]
    private static partial void LogUnknownResolved(ILogger logger, Guid attemptId, string? status);
}
