namespace WaBulk.Domain.Messaging;

/// <summary>
/// Estado de entrega por wamid. Solo avanza: Accepted → Sent → Delivered → Read; Failed es terminal.
/// El orden numérico refleja el avance (se usa en F0.7).
/// </summary>
public enum DeliveryStatus
{
    Accepted = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 9,
}
