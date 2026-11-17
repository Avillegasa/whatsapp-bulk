namespace WaBulk.Domain.Consent;

/// <summary>Una promoción ya enviada al contacto: aceptada por Meta, o rechazada con 131049.</summary>
/// <param name="SentUtc">Cuándo se envió.</param>
/// <param name="Interacted">El contacto la leyó o escribió (respuesta, botón) antes de la siguiente promoción.</param>
/// <param name="Rejected131049">Meta no la entregó para cuidar el ecosistema (131049).</param>
public sealed record PromotionRecord(DateTime SentUtc, bool Interacted, bool Rejected131049);

/// <summary>Parámetros de D-012 (configurables en la sección "Fatiga" del Worker).</summary>
public sealed record FatigueSettings(int Streak = 3, int PauseDays = 60, int Consecutive131049 = 2);

public enum FatigueState { Ok, Paused, Excluded }

public sealed record FatigueResult(FatigueState State, DateTime? PausedUntilUtc = null)
{
    public static readonly FatigueResult Ok = new(FatigueState.Ok);
}

/// <summary>
/// Regla de fatiga (D-012, docs/ARQUITECTURA.md "Fatiga por contacto"): 3 promociones seguidas sin interacción → pausa
/// de promociones 60 días; si la siguiente (después de la pausa) tampoco tiene interacción → excluido de promociones.
/// Dos 131049 seguidos → la misma pausa. Se calcula con el historial en cada envío: si el contacto vuelve a interactuar,
/// la racha se corta sola.
/// </summary>
public static class Fatigue
{
    /// <param name="newestFirst">Promociones del contacto, la más reciente primero.</param>
    public static FatigueResult Evaluate(IReadOnlyList<PromotionRecord> newestFirst, DateTime nowUtc, FatigueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        ArgumentNullException.ThrowIfNull(settings);
        var pause = TimeSpan.FromDays(settings.PauseDays);

        // 131049 seguidos (los más recientes).
        var rejectedStreak = newestFirst.TakeWhile(p => p.Rejected131049).Count();
        if (rejectedStreak >= settings.Consecutive131049)
        {
            var until = newestFirst[0].SentUtc + pause;
            if (nowUtc < until)
            {
                return new FatigueResult(FatigueState.Paused, until);
            }
        }

        // Racha de promociones entregadas sin interacción (los 131049 no se entregaron: no cuentan para esta racha).
        var delivered = newestFirst.Where(p => !p.Rejected131049).ToList();
        var streak = delivered.TakeWhile(p => !p.Interacted).Count();
        if (streak > settings.Streak)
        {
            return new FatigueResult(FatigueState.Excluded); // Ya tuvo la pausa y la siguiente tampoco tuvo interacción.
        }

        if (streak == settings.Streak)
        {
            var until = delivered[0].SentUtc + pause;
            return nowUtc < until ? new FatigueResult(FatigueState.Paused, until) : FatigueResult.Ok; // Pasada la pausa: una más.
        }

        return FatigueResult.Ok;
    }
}
