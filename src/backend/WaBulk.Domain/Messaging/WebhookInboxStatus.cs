namespace WaBulk.Domain.Messaging;

/// <summary>
/// Estado de una fila de WebhookInbox. Los nombres son los de docs/ARQUITECTURA.md
/// porque se guardan tal cual en la base de datos.
/// </summary>
public enum WebhookInboxStatus
{
    Pendiente,
    Procesado,
    EnEspera,
    Revision,
}
