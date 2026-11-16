using Microsoft.Extensions.Logging;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>
/// Envía una plantilla para un <see cref="DispatchJob"/> siguiendo el protocolo anti-duplicados (D-010):
/// 1) inserta y confirma el SendAttempt (Submitting); 2) POST con biz_opaque_callback_data = AttemptId;
/// 3) guarda el resultado clasificado. Si el paso 3 no llega a ejecutarse, el intento queda en Submitting
/// y la recuperación del lease (F0.9) lo pasa a SubmissionUnknown.
/// El estado del DispatchJob y la validación del lease los agrega el despachador (F0.9).
/// </summary>
public sealed partial class TemplateSendService(
    WaBulkDbContext db,
    MetaWhatsAppClient meta,
    TimeProvider clock,
    ILogger<TemplateSendService> logger)
{
    public async Task<SendAttempt> SendAsync(DispatchJob job, TemplateMessage template, CancellationToken cancellationToken)
    {
        var attempt = new SendAttempt
        {
            AttemptId = Guid.CreateVersion7(),
            DispatchJobId = job.Id,
            StartedUtc = clock.GetUtcNow().UtcDateTime,
            Status = SendAttemptStatus.Submitting,
        };
        db.SendAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken); // Confirmado ANTES del POST.

        var result = await meta.SendTemplateAsync(job.PhoneE164, template, attempt.AttemptId, cancellationToken);

        attempt.Status = result.Status;
        attempt.Wamid = result.Wamid;
        attempt.HttpStatus = result.HttpStatus;
        attempt.ErrorCode = result.ErrorCode;
        attempt.Detail = result.Detail;
        attempt.FinishedUtc = clock.GetUtcNow().UtcDateTime;

        // El POST ya ocurrió: el resultado se guarda aunque se haya pedido cancelar.
        await db.SaveChangesAsync(CancellationToken.None);

        LogResult(logger, attempt.AttemptId, attempt.Status, attempt.Wamid, attempt.ErrorCode);
        return attempt;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Intento {AttemptId}: {Status} wamid={Wamid} código={ErrorCode}")]
    private static partial void LogResult(ILogger logger, Guid attemptId, SendAttemptStatus status, string? wamid, int? errorCode);
}
