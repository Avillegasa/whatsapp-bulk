namespace WaBulk.Domain.Messaging;

/// <summary>Webhook recibido tal cual, guardado antes de responder 200. Solo lo escribe el receptor.</summary>
public class WebhookInbox
{
    public long Id { get; set; }
    public DateTime ReceivedUtc { get; set; }
    /// <summary>Cuerpo crudo (JSON) tal como llegó.</summary>
    public required string Body { get; set; }
    public WebhookInboxStatus Status { get; set; } = WebhookInboxStatus.Pendiente;
    public int Attempts { get; set; }
    public string? Error { get; set; }
}
