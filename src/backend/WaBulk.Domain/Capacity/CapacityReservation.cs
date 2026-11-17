namespace WaBulk.Domain.Capacity;

public enum ReservationStatus
{
    /// <summary>Tomada antes del POST; el envío está en curso.</summary>
    Reservada,
    /// <summary>Meta aceptó (o no se sabe: SubmissionUnknown cuenta, por prudencia).</summary>
    Consumida,
    /// <summary>No se envió o Meta rechazó: no ocupa capacidad.</summary>
    Liberada,
}

/// <summary>
/// Una reserva de capacidad por trabajo (F0.11). El límite de Meta cuenta destinatarios ÚNICOS en una ventana móvil
/// de 24 h por portafolio: dos trabajos al mismo número dentro de la ventana ocupan un solo lugar (se cuenta
/// COUNT(DISTINCT PhoneE164) de las reservas Reservada/Consumida con ExpiraUtc vigente).
/// https://developers.facebook.com/documentation/business-messaging/whatsapp/messaging-limits
/// </summary>
public class CapacityReservation
{
    public long Id { get; set; }
    public required string PortfolioId { get; set; }
    public required string PhoneE164 { get; set; }
    public long DispatchJobId { get; set; }
    public DateTime ReservedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Reservada;
}

/// <summary>Resultado de pedir capacidad para un destinatario.</summary>
public enum ReserveOutcome
{
    /// <summary>Número nuevo en la ventana: ocupó un lugar del presupuesto.</summary>
    Reserved,
    /// <summary>El número ya tenía una reserva vigente: no ocupa un lugar nuevo.</summary>
    SameRecipient,
    /// <summary>Presupuesto agotado (o no se obtuvo el bloqueo a tiempo): no se envía en esta vuelta.</summary>
    NoCapacity,
}

/// <summary>
/// Presupuesto del sistema = límite del portafolio × porcentaje de uso (90 %) − cupo manual (D-011).
/// Ejemplo: TIER_250 → 250 × 0,9 = 225 − 200 = 25 destinatarios por 24 h para el sistema.
/// </summary>
public static class CapacityBudget
{
    public static int Compute(int portfolioLimit, int usagePercent, int manualQuota)
    {
        var usable = (int)Math.Floor(portfolioLimit * (usagePercent / 100.0));
        return Math.Max(0, usable - manualQuota);
    }
}
