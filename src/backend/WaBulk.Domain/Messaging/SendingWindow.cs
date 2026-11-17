namespace WaBulk.Domain.Messaging;

/// <summary>
/// Horario permitido para enviar promociones (docs/ARQUITECTURA.md "Controles del despachador": inicial 08:00–20:00,
/// America/La_Paz). Desde incluido, Hasta excluido. Si Desde == Hasta, sin restricción (todo el día). Si Desde > Hasta, el
/// horario cruza la medianoche (por ejemplo 22:00–06:00).
/// </summary>
public sealed class SendingWindow(TimeOnly from, TimeOnly until, TimeZoneInfo zone)
{
    public TimeOnly From { get; } = from;
    public TimeOnly Until { get; } = until;
    public TimeZoneInfo Zone { get; } = zone ?? throw new ArgumentNullException(nameof(zone));

    public bool AlwaysOpen => From == Until;

    public bool IsOpen(DateTime utc)
    {
        if (AlwaysOpen)
        {
            return true;
        }

        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));
        return From < Until ? local >= From && local < Until : local >= From || local < Until;
    }

    /// <summary>Hora local de <paramref name="utc"/> en la zona del horario (para los mensajes).</summary>
    public DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>Crea el horario a partir de la configuración ("08:00", "20:00", "America/La_Paz").</summary>
    public static SendingWindow Parse(string from, string until, string timeZoneId) =>
        new(TimeOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            TimeOnly.Parse(until, System.Globalization.CultureInfo.InvariantCulture),
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
}
