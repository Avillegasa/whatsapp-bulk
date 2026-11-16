namespace WaBulk.Domain.Messaging;

/// <summary>Un trabajo de la cola: un destinatario de una versión de campaña.</summary>
public class DispatchJob
{
    public long Id { get; set; }
    public long CampaignVersionId { get; set; }
    public long ContactId { get; set; }
    public required string PhoneE164 { get; set; }
    /// <summary>Parámetros de la plantilla en JSON.</summary>
    public string? Parameters { get; set; }
    public int Priority { get; set; }
    public DateTime ScheduledUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DispatchStatus DispatchStatus { get; set; } = DispatchStatus.Queued;
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }

    public ICollection<SendAttempt> SendAttempts { get; } = [];
}
