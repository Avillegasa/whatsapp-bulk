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

    /// <summary>
    /// F1.6: campaña en pausa. Fuera del filtro del reclamo (Queued/RetryScheduled), así el despachador no lo vuelve a
    /// tomar en cada vuelta; al reanudar vuelve a Queued (o RetryScheduled si tenía ProximoIntentoUtc).
    /// </summary>
    OnHold,
}
