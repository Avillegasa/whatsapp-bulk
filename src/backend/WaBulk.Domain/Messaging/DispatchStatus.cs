namespace WaBulk.Domain.Messaging;

/// <summary>Estado de despacho de un <see cref="DispatchJob"/> (columna EstadoDespacho). Ver docs/ARQUITECTURA.md "Estados".</summary>
public enum DispatchStatus
{
    Queued,
    Claimed,
    Submitted,
    RetryScheduled,
    NeedsReview,
    Failed,
    Skipped,
    Expired,
}
