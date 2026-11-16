namespace WaBulk.Domain.Messaging;

/// <summary>Evento de estado recibido por webhook para un wamid. Único por (Wamid, Estado).</summary>
public class MessageEvent
{
    public long Id { get; set; }
    public required string Wamid { get; set; }
    public Guid? AttemptId { get; set; }
    public DeliveryStatus Status { get; set; }
    /// <summary>Marca de tiempo informada por Meta (UTC).</summary>
    public DateTime MetaTimestampUtc { get; set; }
    /// <summary>Objeto pricing del webhook en JSON.</summary>
    public string? Pricing { get; set; }
    /// <summary>Arreglo errors del webhook en JSON.</summary>
    public string? Errors { get; set; }
    public DateTime CreatedUtc { get; set; }
}
