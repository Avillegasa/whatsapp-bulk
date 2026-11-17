namespace WaBulk.Domain.Templates;

/// <summary>
/// Plantilla de WhatsApp (tabla Template, ARQUITECTURA): copia local de lo que Meta tiene en la WABA, sincronizada con
/// GET /{WABA_ID}/message_templates y actualizada por los webhooks message_template_status_update,
/// message_template_quality_update y template_category_update (F1.5).
/// Estado, Calidad y Categoria se guardan con el texto de Meta (APPROVED, GREEN, MARKETING…): Meta suma valores nuevos y
/// no conviene que un valor desconocido rompa la lectura. Los estados propios (antes de que Meta conteste) están en
/// <see cref="TemplateStatuses"/>.
/// </summary>
public class Template
{
    public long Id { get; set; }

    /// <summary>Id de Meta. NULL mientras se crea o si la creación quedó incierta.</summary>
    public string? MetaTemplateId { get; set; }

    public required string Name { get; set; }

    /// <summary>Código de idioma como lo usa la API de plantillas: "es", "en_US" (los webhooks lo mandan con guion: "en-US").</summary>
    public required string Language { get; set; }

    /// <summary>MARKETING, UTILITY o AUTHENTICATION.</summary>
    public required string Category { get; set; }

    /// <summary>Estado de Meta (APPROVED, PENDING, REJECTED, PAUSED, DISABLED…) o uno propio de <see cref="TemplateStatuses"/>.</summary>
    public required string Status { get; set; }

    /// <summary>Hora del dato de estado (entry.time del webhook o la hora de la sincronización). Un webhook más viejo no lo pisa.</summary>
    public DateTime StatusUtc { get; set; }

    /// <summary>Motivo de Meta (reason, rejected_reason, other_info) o detalle de un error al crearla.</summary>
    public string? StatusReason { get; set; }

    /// <summary>Último evento recibido por webhook (APPROVED, FLAGGED, REINSTATED…), aunque no cambie el estado.</summary>
    public string? LastEvent { get; set; }

    /// <summary>GREEN, YELLOW, RED o UNKNOWN. NULL si Meta aún no la informó.</summary>
    public string? Quality { get; set; }

    public DateTime? QualityUtc { get; set; }

    /// <summary>Categoría anterior a una recategorización de Meta.</summary>
    public string? PreviousCategory { get; set; }

    /// <summary>Recategorización anunciada (template_category_update con correct_category): a qué categoría pasará.</summary>
    public string? CorrectCategory { get; set; }

    /// <summary>Cuándo se aplica la recategorización anunciada.</summary>
    public DateTime? CategoryChangeUtc { get; set; }

    /// <summary>NAMED o POSITIONAL.</summary>
    public string? ParameterFormat { get; set; }

    /// <summary>components en JSON, como los devuelve Meta (o como se enviaron al crearla, hasta sincronizar).</summary>
    public string? Components { get; set; }

    /// <summary>Última vez que la sincronización la encontró en Meta.</summary>
    public DateTime? SyncedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Usuario de la intranet que la creó, o "meta" si llegó por sincronización o por webhook.</summary>
    public required string CreatedBy { get; set; }
}

/// <summary>Estados de Meta que usa el sistema y estados propios del proceso de creación.</summary>
public static class TemplateStatuses
{
    public const string Approved = "APPROVED";
    public const string Pending = "PENDING";
    public const string Rejected = "REJECTED";
    public const string Paused = "PAUSED";

    /// <summary>Guardada y auditada; el POST a Meta está en curso (como Submitting en SendAttempt).</summary>
    public const string Creating = "CREANDO";

    /// <summary>Sin respuesta confiable de Meta al crearla: la sincronización dirá si existe (regla 4).</summary>
    public const string Unknown = "INCIERTA";

    /// <summary>Meta rechazó la petición de creación (nombre repetido, formato…): no existe en Meta.</summary>
    public const string CreateFailed = "ERROR_CREACION";

    /// <summary>Tenía MetaTemplateId y la sincronización ya no la encuentra en la WABA.</summary>
    public const string NotFound = "NO_ENCONTRADA";

    /// <summary>
    /// Eventos de message_template_status_update que son también un estado de la plantilla (Message Template API, campo
    /// status). Los demás (FLAGGED, LOCKED, REINSTATED, UNARCHIVED…) se anotan en LastEvent sin adivinar el estado: la
    /// sincronización trae el estado real.
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/message_template_status_update
    /// </summary>
    public static readonly IReadOnlySet<string> FromMeta = new HashSet<string>(StringComparer.Ordinal)
    {
        "APPROVED", "ARCHIVED", "DELETED", "DISABLED", "IN_APPEAL", "LIMIT_EXCEEDED", "PAUSED", "PENDING",
        "PENDING_DELETION", "REJECTED",
    };

    /// <summary>Estados propios que permiten volver a intentar la creación con el mismo nombre e idioma.</summary>
    public static bool CanRetryCreate(string status) => status is CreateFailed or Unknown or NotFound;
}

/// <summary>Categorías de Meta. En la primera etapa solo se crean de marketing (D-009).</summary>
public static class TemplateCategories
{
    public const string Marketing = "MARKETING";
}
