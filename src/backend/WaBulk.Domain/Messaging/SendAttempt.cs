namespace WaBulk.Domain.Messaging;

/// <summary>
/// Un intento de POST a /messages. Se inserta y confirma antes del POST;
/// su AttemptId viaja como biz_opaque_callback_data (D-010).
/// </summary>
public class SendAttempt
{
    public Guid AttemptId { get; set; }
    public long DispatchJobId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public SendAttemptStatus Status { get; set; } = SendAttemptStatus.Submitting;
    /// <summary>Id del mensaje en WhatsApp; solo existe si Meta aceptó.</summary>
    public string? Wamid { get; set; }
    public int? HttpStatus { get; set; }
    public int? ErrorCode { get; set; }
    public string? Detail { get; set; }

    public DispatchJob? DispatchJob { get; set; }
}
