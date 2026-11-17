namespace WaBulk.Domain.Campaigns;

/// <summary>
/// Estados de una campaña (docs/ARQUITECTURA.md "Estados"): Draft → PendingApproval → Scheduled → Running →
/// DispatchCompleted → TrackingClosed; laterales Paused, Cancelled y Expired.
/// </summary>
public enum CampaignStatus
{
    Draft,
    PendingApproval,
    Scheduled,
    Running,
    Paused,
    DispatchCompleted,
    TrackingClosed,
    Cancelled,
    Expired,
}

/// <summary>
/// Campaña (F1.6). Lo que se envía vive en <see cref="CampaignVersion"/>, que no cambia nunca: editar crea una
/// versión nueva que exige aprobación. La campaña solo guarda su estado y cuál es la versión vigente.
/// </summary>
public class Campaign
{
    public long Id { get; set; }

    public required string Name { get; set; }

    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

    /// <summary>Versión vigente: la última creada. Las anteriores quedan como historial.</summary>
    public long? CurrentVersionId { get; set; }

    /// <summary>Motivo del último cambio manual (rechazo, pausa, cancelación).</summary>
    public string? StatusReason { get; set; }

    public DateTime StatusUtc { get; set; }

    /// <summary>Cuándo terminó el despacho (DispatchCompleted); el seguimiento se cierra un plazo después.</summary>
    public DateTime? CompletedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public required string CreatedBy { get; set; }
}

/// <summary>
/// Versión inmutable de una campaña: plantilla (copia de nombre, idioma, categoría y formato al crearla), audiencia
/// congelada (Ids de contacto), definición de parámetros, programación, vencimiento y Hash de todo eso. Solo los
/// campos de envío a aprobación, aprobación y rechazo se completan después, una sola vez.
/// </summary>
public class CampaignVersion
{
    public long Id { get; set; }
    public long CampaignId { get; set; }

    /// <summary>1, 2, 3… dentro de la campaña.</summary>
    public int Number { get; set; }

    public long TemplateId { get; set; }
    public required string TemplateName { get; set; }
    public required string Language { get; set; }
    public required string Category { get; set; }

    /// <summary>NAMED o POSITIONAL, copiado de la plantilla.</summary>
    public required string ParameterFormat { get; set; }

    /// <summary>Lista de origen (Segment). La audiencia real es <see cref="Audience"/>, copiada al crear la versión.</summary>
    public long? SegmentId { get; set; }

    /// <summary>JSON con los Ids de contacto congelados, ordenados.</summary>
    public required string Audience { get; set; }

    public int AudienceCount { get; set; }

    /// <summary>Miembros de la lista que no entraron (sin teléfono o inactivos al crear la versión).</summary>
    public int ExcludedCount { get; set; }

    /// <summary>JSON con la definición de parámetros (<see cref="CampaignParameter"/>), en el orden del cuerpo.</summary>
    public required string Parameters { get; set; }

    public DateTime ScheduledUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }

    /// <summary>SHA-256 (hex) del contenido: lo que ve y aprueba el aprobador.</summary>
    public required string Hash { get; set; }

    public DateTime CreatedUtc { get; set; }
    public required string CreatedBy { get; set; }

    public DateTime? SubmittedUtc { get; set; }
    public DateTime? ApprovedUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? RejectedUtc { get; set; }
    public string? RejectedBy { get; set; }
    public string? RejectionReason { get; set; }
}

/// <summary>Origen del valor de un parámetro de la plantilla.</summary>
public enum ParameterSource
{
    /// <summary>El mismo texto para todos.</summary>
    Fijo,

    /// <summary>Nombre del contacto; si no tiene, <see cref="CampaignParameter.Value"/>.</summary>
    NombreContacto,
}

/// <summary>Un parámetro de la campaña: {{promocion}} = "2x1" (Fijo) o {{nombre}} = nombre del contacto (o "cliente").</summary>
public sealed record CampaignParameter(string Name, ParameterSource Source, string Value);

/// <summary>Valor ya resuelto para un contacto (DispatchJob.Parametros).</summary>
public sealed record JobParameter(string Name, string Value);
