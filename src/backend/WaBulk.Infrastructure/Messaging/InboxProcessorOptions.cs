namespace WaBulk.Infrastructure.Messaging;

/// <summary>Configuración de la sección "InboxProcessor" (procesador de WebhookInbox, F0.7).</summary>
public sealed class InboxProcessorOptions
{
    public const string SectionName = "InboxProcessor";

    /// <summary>Filas leídas por vuelta.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Espera entre vueltas cuando no quedan filas por procesar.</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>Intentos antes de pasar una fila EnEspera a Revision.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Primera espera de una fila EnEspera; se duplica en cada intento.</summary>
    public int RetryDelaySeconds { get; set; } = 30;

    /// <summary>Tope de la espera entre intentos.</summary>
    public int MaxRetryDelaySeconds { get; set; } = 600;
}
