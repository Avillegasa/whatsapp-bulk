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

    /// <summary>Plantilla del PoC mientras no exista CampaignVersion (Fase 1 la toma de la versión de campaña).</summary>
    public string TemplateName { get; set; } = "hello_world";
    public string TemplateLanguage { get; set; } = "en_US";

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    public RetrySettings Retry => new(MaxAttempts, TimeSpan.FromSeconds(RetryBaseSeconds), TimeSpan.FromSeconds(RetryMaxSeconds));
}
