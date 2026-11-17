using Microsoft.Extensions.Options;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>
/// Tasa de envío por número (F1.7, docs/ARQUITECTURA.md "Tasa por número": inicial 10–20 mensajes/s; Meta admite hasta 80
/// por número). Token bucket por reserva: cada envío toma una ficha; si no hay, espera lo que falta para que se recargue.
/// Una sola instancia por proceso (singleton): el Worker usa un solo número (Meta:PhoneNumberId). Con varios Worker, cada
/// uno tiene su cubeta: la tasa total sería la suma (configurar en consecuencia).
/// </summary>
public sealed class SendRateLimiter(IOptions<DispatcherOptions> options, TimeProvider clock)
{
    private readonly Lock _gate = new();
    private readonly double _rate = options.Value.MessagesPerSecond;
    private readonly double _burst = Math.Max(1, options.Value.RateBurst);
    private double _tokens = Math.Max(1, options.Value.RateBurst);
    private long _lastTicks = clock.GetTimestamp();

    /// <summary>Toma una ficha y devuelve cuánto hay que esperar antes de usarla (cero si había).</summary>
    public TimeSpan Reserve()
    {
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            var elapsed = clock.GetElapsedTime(_lastTicks, now).TotalSeconds;
            _lastTicks = now;
            _tokens = Math.Min(_burst, _tokens + (elapsed * _rate)) - 1;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
    }

    /// <summary>Espera su turno antes de un POST a /messages.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var wait = Reserve();
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, clock, cancellationToken);
        }
    }
}
