using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;

namespace WaBulk.Tests.Consent;

/// <summary>Fatiga (D-012) y 131049 (+24 h, un intento más), sin base de datos (F1.7).</summary>
public sealed class FatigueTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
    private static readonly FatigueSettings Settings = new();

    private static PromotionRecord Sent(int daysAgo, bool interacted = false) => new(Now.AddDays(-daysAgo), interacted, false);

    private static PromotionRecord Rejected(int daysAgo) => new(Now.AddDays(-daysAgo), false, true);

    [Fact]
    public void Dos_sin_interaccion_sigue_recibiendo()
    {
        Assert.Equal(FatigueState.Ok, Fatigue.Evaluate([Sent(3), Sent(10)], Now, Settings).State);
    }

    [Fact]
    public void Tres_sin_interaccion_pausa_60_dias_desde_la_ultima()
    {
        var r = Fatigue.Evaluate([Sent(3), Sent(10), Sent(17)], Now, Settings);
        Assert.Equal((FatigueState.Paused, Now.AddDays(57)), (r.State, r.PausedUntilUtc));
    }

    [Fact]
    public void Pasada_la_pausa_sale_una_mas_y_si_tampoco_interactua_queda_excluido()
    {
        Assert.Equal(FatigueState.Ok, Fatigue.Evaluate([Sent(61), Sent(68), Sent(75)], Now, Settings).State);
        Assert.Equal(FatigueState.Excluded, Fatigue.Evaluate([Sent(1), Sent(61), Sent(68), Sent(75)], Now, Settings).State);
    }

    [Fact]
    public void Una_interaccion_corta_la_racha()
    {
        Assert.Equal(FatigueState.Ok, Fatigue.Evaluate([Sent(3), Sent(10, interacted: true), Sent(17), Sent(24)], Now, Settings).State);
        Assert.Equal(FatigueState.Ok, Fatigue.Evaluate([Sent(3, interacted: true), Sent(10), Sent(17), Sent(24), Sent(31)], Now, Settings).State);
    }

    [Fact]
    public void Dos_131049_seguidos_pausan_y_no_cuentan_como_entregados()
    {
        var r = Fatigue.Evaluate([Rejected(1), Rejected(2), Sent(10, interacted: true)], Now, Settings);
        Assert.Equal((FatigueState.Paused, Now.AddDays(59)), (r.State, r.PausedUntilUtc));
        Assert.Equal(FatigueState.Ok, Fatigue.Evaluate([Rejected(1), Sent(10, interacted: true)], Now, Settings).State);
        // Un 131049 entre promociones sin interacción no corta ni alarga la racha de "sin interacción".
        Assert.Equal(FatigueState.Ok, Fatigue.Evaluate([Rejected(1), Sent(10), Sent(17)], Now, Settings).State);
    }

    [Fact]
    public void Error_131049_reintenta_una_vez_a_las_24_horas_si_sigue_vigente()
    {
        var settings = new RetrySettings(5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));
        var first = DispatchRetryPolicy.After(SendAttemptStatus.Rejected, 131049, 1, Now.AddDays(2), Now, settings, 0.5);
        Assert.Equal((DispatchStatus.RetryScheduled, Now.AddHours(24)), (first.Status, first.NextAttemptUtc));

        var second = DispatchRetryPolicy.After(SendAttemptStatus.Rejected, 131049, 2, Now.AddDays(2), Now, settings, 0.5, previous131049: true);
        Assert.Equal(DispatchStatus.Failed, second.Status);

        var expiring = DispatchRetryPolicy.After(SendAttemptStatus.Rejected, 131049, 1, Now.AddHours(12), Now, settings, 0.5);
        Assert.Equal(DispatchStatus.Expired, expiring.Status);
    }
}
