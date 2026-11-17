using WaBulk.Domain.Alerts;

namespace WaBulk.Domain.Messaging;

/// <summary>Parámetros de reintento (sección "Dispatcher" del Worker).</summary>
/// <param name="MaxAttempts">Máximo de intentos por trabajo, contando el primero (regla 9: 5).</param>
/// <param name="BaseDelay">Espera del primer reintento; se duplica en cada uno.</param>
/// <param name="MaxDelay">Tope de la espera.</param>
public sealed record RetrySettings(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay);

/// <summary>Estado del trabajo tras un intento y, si se reintenta, cuándo.</summary>
public sealed record DispatchTransition(DispatchStatus Status, DateTime? NextAttemptUtc);

/// <summary>
/// Qué pasa con un <see cref="DispatchJob"/> después de un intento (docs/ARQUITECTURA.md "Resultado de un intento" y "Errores").
/// Regla 9: solo se reintentan los códigos transitorios, con backoff exponencial y jitter, máximo 5 intentos
/// y nunca después de ExpiraUtc. Un SubmissionUnknown nunca se reintenta (regla 4).
/// https://developers.facebook.com/documentation/business-messaging/whatsapp/support/error-codes
/// </summary>
public static class DispatchRetryPolicy
{
    /// <summary>4 y 80007/130429 (límites de tasa), 131056 (par emisor-destinatario), 131000 y 131016 (servicio).</summary>
    public static readonly IReadOnlySet<int> TransientCodes = new HashSet<int> { 4, 80007, 130429, 131056, 131000, 131016 };

    public static bool IsTransient(int? errorCode) => errorCode is { } code && TransientCodes.Contains(code);

    /// <param name="attempts">Intentos hechos, incluido el que acaba de terminar.</param>
    /// <param name="jitter">Valor en [0, 1): la espera real queda entre la mitad y el total del backoff.</param>
    public static DispatchTransition After(
        SendAttemptStatus result, int? errorCode, int attempts, DateTime expiresUtc, DateTime nowUtc,
        RetrySettings settings, double jitter)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (result)
        {
            case SendAttemptStatus.Accepted:
                return new(DispatchStatus.Submitted, null);
            case SendAttemptStatus.SubmissionUnknown:
                return new(DispatchStatus.NeedsReview, null);
            case SendAttemptStatus.Rejected when EmergencyBrake.ForErrorCode(errorCode) is not null:
                // 190, 131042, 132015: el problema es nuestro (token, pago, plantilla), no del destinatario. El trabajo
                // vuelve a la cola y el despacho queda en pausa global hasta que una persona la levante (F0.12).
                return new(DispatchStatus.Queued, null);
            case SendAttemptStatus.Rejected when IsTransient(errorCode) && attempts < settings.MaxAttempts:
                var next = nowUtc + Backoff(attempts, settings, jitter);
                return next < expiresUtc
                    ? new(DispatchStatus.RetryScheduled, next)
                    : new(DispatchStatus.Expired, null);
            case SendAttemptStatus.Rejected:
                return new(DispatchStatus.Failed, null);
            default:
                throw new ArgumentOutOfRangeException(nameof(result), result, "Un intento terminado no puede quedar en Submitting.");
        }
    }

    /// <summary>Base × 2^(intentos−1), con tope, y "equal jitter": entre la mitad y el total.</summary>
    public static TimeSpan Backoff(int attempts, RetrySettings settings, double jitter)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var full = Math.Min(settings.BaseDelay.TotalSeconds * Math.Pow(2, exponent), settings.MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(full / 2 + Math.Clamp(jitter, 0, 1) * full / 2);
    }
}
