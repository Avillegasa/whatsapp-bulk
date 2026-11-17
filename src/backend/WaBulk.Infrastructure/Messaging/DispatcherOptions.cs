using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Messaging;

/// <summary>Configuración de la sección "Dispatcher" (F0.9).</summary>
public sealed class DispatcherOptions
{
    public const string SectionName = "Dispatcher";

    /// <summary>
    /// Apagado por defecto: en la Fase 0 no hay campañas y un trabajo en cola enviaría un mensaje real.
    /// Se enciende con Dispatcher:Enabled=true.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Dueño del lease (LockedBy). Vacío: nombre de la máquina + id del proceso + sufijo aleatorio.</summary>
    public string? WorkerId { get; set; }

    public int BatchSize { get; set; } = 10;
    public int PollSeconds { get; set; } = 2;

    /// <summary>
    /// Duración del lease. Se renueva al empezar cada trabajo y debe superar con margen el timeout del POST
    /// (Meta:RequestTimeoutSeconds): si venciera durante un POST, la recuperación marcaría SubmissionUnknown.
    /// </summary>
    public int LeaseSeconds { get; set; } = 120;

    public int MaxAttempts { get; set; } = 5;
    public int RetryBaseSeconds { get; set; } = 30;
    public int RetryMaxSeconds { get; set; } = 1800;

    /// <summary>Cuánto se pospone un trabajo que no consiguió capacidad del portafolio (F0.11).</summary>
    public int NoCapacityRetryMinutes { get; set; } = 5;

    /// <summary>
    /// Tasa por número (F1.7): mensajes por segundo, 1 a 80 (techo de Meta por número). Inicial 10.
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/throughput
    /// </summary>
    public int MessagesPerSecond { get; set; } = 10;

    /// <summary>Fichas acumulables del token bucket (1 = envíos parejos, sin ráfagas).</summary>
    public int RateBurst { get; set; } = 1;

    /// <summary>
    /// Horario permitido (F1.7), hora local de <see cref="TimeZone"/>: desde incluido, hasta excluido. Iguales = todo el
    /// día (valor por defecto de la clase; el appsettings del Worker pone 08:00–20:00).
    /// </summary>
    public string AllowedFrom { get; set; } = "00:00";

    public string AllowedUntil { get; set; } = "00:00";

    /// <summary>Zona IANA del horario. Bolivia: America/La_Paz (UTC−4, sin horario de verano).</summary>
    public string TimeZone { get; set; } = "America/La_Paz";

    /// <summary>Freno por tasa de fallos (F1.7): últimos N envíos de campañas desde la última reanudación.</summary>
    public int FailureWindow { get; set; } = 200;

    /// <summary>Envíos mínimos para evaluar la tasa de fallos.</summary>
    public int FailureMinSample { get; set; } = 100;

    /// <summary>Porcentaje de fallos que pausa todo (docs/ARQUITECTURA.md: 5 %).</summary>
    public int FailureThresholdPercent { get; set; } = 5;

    /// <summary>Horas sin webhook tras las que un NeedsReview se lista para revisión manual (F1.7; inicial 2).</summary>
    public int NeedsReviewHours { get; set; } = 2;

    /// <summary>Fatiga (F1.7, D-012): promociones seguidas sin interacción que pausan al contacto.</summary>
    public int FatigueStreak { get; set; } = 3;

    /// <summary>Días de pausa por fatiga.</summary>
    public int FatiguePauseDays { get; set; } = 60;

    /// <summary>131049 seguidos que pausan al contacto.</summary>
    public int FatigueConsecutive131049 { get; set; } = 2;

    public FatigueSettings Fatigue => new(FatigueStreak, FatiguePauseDays, FatigueConsecutive131049);

    public SendingWindow Window => SendingWindow.Parse(AllowedFrom, AllowedUntil, TimeZone);

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    public RetrySettings Retry => new(MaxAttempts, TimeSpan.FromSeconds(RetryBaseSeconds), TimeSpan.FromSeconds(RetryMaxSeconds));
}

/// <summary>
/// Tope de promociones por contacto (F1.7, D-031). Misma clave que usa la Api para el texto de la oferta
/// ("OfertaPromociones:MaxPromotionsPerWeek"): lo que se promete al cliente es lo que cumple el despachador.
/// </summary>
public sealed class FrequencyOptions
{
    public const string SectionName = "OfertaPromociones";

    /// <summary>Promociones como máximo por contacto en 7 días móviles. Inicial 2.</summary>
    public int MaxPromotionsPerWeek { get; set; } = 2;
}
