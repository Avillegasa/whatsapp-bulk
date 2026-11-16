namespace WaBulk.Domain.Messaging;

/// <summary>Resultado de un intento de envío. Ver docs/ARQUITECTURA.md "Resultado de un intento".</summary>
public enum SendAttemptStatus
{
    /// <summary>Insertado y confirmado antes del POST; aún sin respuesta de Meta.</summary>
    Submitting,
    Accepted,
    Rejected,
    /// <summary>Sin respuesta confiable: nunca se reenvía automáticamente.</summary>
    SubmissionUnknown,
}
