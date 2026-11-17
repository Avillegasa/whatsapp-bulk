namespace WaBulk.Domain.Messaging;

/// <summary>Un trabajo de la cola: un destinatario de una versión de campaña.</summary>
public class DispatchJob
{
    public long Id { get; set; }
    /// <summary>Versión de campaña (FK, F1.6). NULL solo en los trabajos de send-test, que nunca pasan por la cola.</summary>
    public long? CampaignVersionId { get; set; }
    public long ContactId { get; set; }
    public required string PhoneE164 { get; set; }
    /// <summary>Valores de los parámetros para este contacto: JSON [{"Name":"nombre","Value":"Ana"}…] en el orden del cuerpo.</summary>
    public string? Parameters { get; set; }
    public int Priority { get; set; }
    public DateTime ScheduledUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DispatchStatus DispatchStatus { get; set; } = DispatchStatus.Queued;
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    /// <summary>Por qué quedó Skipped (ver WaBulk.Domain.Consent.SkipReasons). Null en los demás estados.</summary>
    public string? SkipReason { get; set; }

    public ICollection<SendAttempt> SendAttempts { get; } = [];
}
