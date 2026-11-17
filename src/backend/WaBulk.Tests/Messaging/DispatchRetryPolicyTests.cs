using WaBulk.Domain.Messaging;

namespace WaBulk.Tests.Messaging;

/// <summary>Regla 9: reintentos solo para transitorios, backoff con jitter, máximo 5, nunca después de ExpiraUtc.</summary>
public sealed class DispatchRetryPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RetrySettings Settings = new(5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));

    [Theory]
    [InlineData(4)]
    [InlineData(80007)]
    [InlineData(130429)]
    [InlineData(131056)]
    [InlineData(131000)]
    [InlineData(131016)]
    public void Transitorio_se_reprograma(int code)
    {
        var t = DispatchRetryPolicy.After(SendAttemptStatus.Rejected, code, 1, Now.AddDays(1), Now, Settings, 0.5);

        Assert.Equal(DispatchStatus.RetryScheduled, t.Status);
        Assert.NotNull(t.NextAttemptUtc);
    }

    [Theory]
    [InlineData(131026)] // no alcanzable
    [InlineData(131050)] // baja de marketing de la plataforma
    // 131049 (ecosistema) tiene su propio tratamiento desde F1.7: +24 h, un intento más (FatigueTests).
    [InlineData(132001)] // plantilla
    [InlineData(100)]
    public void No_transitorio_falla_sin_reintento(int code)
    {
        var t = DispatchRetryPolicy.After(SendAttemptStatus.Rejected, code, 1, Now.AddDays(1), Now, Settings, 0.5);

        Assert.Equal(new DispatchTransition(DispatchStatus.Failed, null), t);
    }

    [Theory]
    [InlineData(190)]    // token
    [InlineData(131042)] // facturación
    [InlineData(132015)] // plantilla pausada
    public void Freno_de_emergencia_devuelve_el_trabajo_a_la_cola(int code) =>
        Assert.Equal(new DispatchTransition(DispatchStatus.Queued, null),
            DispatchRetryPolicy.After(SendAttemptStatus.Rejected, code, 1, Now.AddDays(1), Now, Settings, 0.5));

    [Fact]
    public void Rechazo_sin_codigo_falla() =>
        Assert.Equal(DispatchStatus.Failed,
            DispatchRetryPolicy.After(SendAttemptStatus.Rejected, null, 1, Now.AddDays(1), Now, Settings, 0).Status);

    [Fact]
    public void Quinto_intento_transitorio_falla()
    {
        Assert.Equal(DispatchStatus.RetryScheduled,
            DispatchRetryPolicy.After(SendAttemptStatus.Rejected, 131000, 4, Now.AddDays(1), Now, Settings, 0).Status);
        Assert.Equal(DispatchStatus.Failed,
            DispatchRetryPolicy.After(SendAttemptStatus.Rejected, 131000, 5, Now.AddDays(1), Now, Settings, 0).Status);
    }

    [Fact]
    public void Reintento_que_cae_despues_de_ExpiraUtc_queda_Expired()
    {
        var t = DispatchRetryPolicy.After(SendAttemptStatus.Rejected, 131000, 1, Now.AddSeconds(10), Now, Settings, 0);

        Assert.Equal(new DispatchTransition(DispatchStatus.Expired, null), t);
    }

    [Fact]
    public void Aceptado_y_sin_respuesta()
    {
        Assert.Equal(DispatchStatus.Submitted,
            DispatchRetryPolicy.After(SendAttemptStatus.Accepted, null, 1, Now.AddDays(1), Now, Settings, 0).Status);
        // Regla 4: nunca reenvío automático.
        Assert.Equal(new DispatchTransition(DispatchStatus.NeedsReview, null),
            DispatchRetryPolicy.After(SendAttemptStatus.SubmissionUnknown, null, 1, Now.AddDays(1), Now, Settings, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DispatchRetryPolicy.After(SendAttemptStatus.Submitting, null, 1, Now.AddDays(1), Now, Settings, 0));
    }

    [Theory]
    [InlineData(1, 15, 30)]     // 30 s
    [InlineData(2, 30, 60)]     // 60 s
    [InlineData(4, 120, 240)]   // 240 s
    [InlineData(12, 900, 1800)] // tope 30 min
    public void Backoff_exponencial_con_jitter_entre_la_mitad_y_el_total(int attempts, double min, double max)
    {
        Assert.Equal(min, DispatchRetryPolicy.Backoff(attempts, Settings, 0).TotalSeconds, 3);
        Assert.Equal(max, DispatchRetryPolicy.Backoff(attempts, Settings, 1).TotalSeconds, 3);
        var mid = DispatchRetryPolicy.Backoff(attempts, Settings, 0.5).TotalSeconds;
        Assert.InRange(mid, min, max);
    }
}
