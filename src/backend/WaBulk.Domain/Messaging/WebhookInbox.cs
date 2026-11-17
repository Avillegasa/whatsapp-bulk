namespace WaBulk.Domain.Messaging;

/// <summary>Webhook recibido tal cual, guardado antes de responder 200. Solo lo escribe el receptor.</summary>
public class WebhookInbox
{
    public long Id { get; set; }
    public DateTime ReceivedUtc { get; set; }
    /// <summary>Cuerpo crudo (JSON) tal como llegó.</summary>
    public required string Body { get; set; }
    public WebhookInboxStatus Status { get; set; } = WebhookInboxStatus.Pendiente;
    /// <summary>Veces que el procesador intentó aplicar la fila.</summary>
    public int Attempts { get; set; }
    /// <summary>Cuándo reintentar una fila EnEspera (estados aún sin correlacionar, D-023).</summary>
    public DateTime? NextAttemptUtc { get; set; }
    public string? Error { get; set; }
}
