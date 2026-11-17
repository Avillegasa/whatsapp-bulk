using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Infrastructure.Templates;

namespace WaBulk.Infrastructure.Campaigns;

/// <summary>Lo que pide el operador para crear una campaña o una versión nueva.</summary>
public sealed record CampaignDraft(
    string? Name, long TemplateId, long SegmentId, IReadOnlyList<CampaignParameter>? Parameters, DateTime ScheduledUtc,
    DateTime ExpiresUtc);

/// <summary>Errores de una acción. <see cref="Prohibido"/> → 403, <see cref="NoExiste"/> → 404, <see cref="Conflicto"/> → 409.</summary>
public static class CampaignErrors
{
    public const string Invalida = "Invalida";
    public const string NoExiste = "NoExiste";
    public const string Estado = "EstadoNoPermite";
    public const string Prohibido = "Prohibido";
    public const string Conflicto = "Conflicto";
}

public sealed record CampaignResult(string? Error, string Message, long? CampaignId = null)
{
    public bool Succeeded => Error is null;

    public static CampaignResult Fail(string error, string message) => new(error, message);
}

public sealed record CampaignSummary(
    long Id, string Name, CampaignStatus Status, DateTime StatusUtc, string? StatusReason, int? VersionNumber,
    string? TemplateName, string? Language, int AudienceCount, DateTime? ScheduledUtc, DateTime? ExpiresUtc, string CreatedBy,
    DateTime CreatedUtc);

public sealed record VersionDetail(
    long Id, int Number, long TemplateId, string TemplateName, string Language, string Category, string ParameterFormat,
    long? SegmentId, string? SegmentName, int AudienceCount, int ExcludedCount, IReadOnlyList<CampaignParameter> Parameters,
    DateTime ScheduledUtc, DateTime ExpiresUtc, string Hash, DateTime CreatedUtc, string CreatedBy, DateTime? SubmittedUtc,
    DateTime? ApprovedUtc, string? ApprovedBy, DateTime? RejectedUtc, string? RejectedBy, string? RejectionReason);

public sealed record JobCount(string Status, string? Reason, int Count);

public sealed record CampaignDetail(
    CampaignSummary Campaign, IReadOnlyList<VersionDetail> Versions, IReadOnlyList<JobCount> Jobs, TemplateContent? Content);

/// <summary>
/// Campañas (F1.6, D-034). Cada acción es una transacción con su fila de AuditLog (D-030). Al aprobar se crea un
/// DispatchJob por destinatario en la misma transacción que el cambio de estado; el UPDATE condicional de la campaña
/// (PendingApproval + versión vigente) hace que de dos aprobaciones simultáneas solo una cree los trabajos, y el índice
/// único (CampaignVersionId, ContactId) es la segunda barrera.
/// </summary>
public sealed partial class CampaignService(
    WaBulkDbContext db, ConsentService consent, TimeProvider clock, ILogger<CampaignService> logger)
{
    private static readonly DispatchStatus[] Pending =
        [DispatchStatus.Queued, DispatchStatus.RetryScheduled, DispatchStatus.OnHold];

    // ---------- Consultas ----------

    public async Task<List<CampaignSummary>> ListAsync(CampaignStatus? status, CancellationToken cancellationToken)
    {
        var rows = await (from c in db.Campaigns.AsNoTracking()
                          join v in db.CampaignVersions.AsNoTracking() on c.CurrentVersionId equals v.Id into vs
                          from v in vs.DefaultIfEmpty()
                          where status == null || c.Status == status
                          orderby c.Id descending
                          select new { c, v }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => ToSummary(r.c, r.v))];
    }

    public async Task<CampaignDetail?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (campaign is null)
        {
            return null;
        }

        var versions = await db.CampaignVersions.AsNoTracking().Where(v => v.CampaignId == id)
            .OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        var segmentIds = versions.Where(v => v.SegmentId != null).Select(v => v.SegmentId!.Value).Distinct().ToList();
        var segments = await db.Segments.AsNoTracking().Where(s => segmentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var current = versions.FirstOrDefault(v => v.Id == campaign.CurrentVersionId);
        var jobs = current is null ? []
            : await db.DispatchJobs.AsNoTracking().Where(j => j.CampaignVersionId == current.Id)
                .GroupBy(j => new { j.DispatchStatus, j.SkipReason })
                .Select(g => new { g.Key.DispatchStatus, g.Key.SkipReason, Count = g.Count() })
                .ToListAsync(cancellationToken);
        var template = current is null ? null
            : await db.Templates.AsNoTracking().Where(t => t.Id == current.TemplateId).Select(t => t.Components)
                .SingleOrDefaultAsync(cancellationToken);

        return new CampaignDetail(
            ToSummary(campaign, current),
            [.. versions.Select(v => ToDetail(v, v.SegmentId is { } s && segments.TryGetValue(s, out var n) ? n : null))],
            [.. jobs.Select(j => new JobCount(j.DispatchStatus.ToString(), j.SkipReason, j.Count))
                .OrderBy(j => j.Status, StringComparer.Ordinal)],
            current is null ? null : TemplateContent.Parse(template));
    }

    // ---------- Operador ----------

    public async Task<CampaignResult> CreateAsync(string actor, CampaignDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var name = draft.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            return CampaignResult.Fail(CampaignErrors.Invalida, "El nombre de la campaña debe tener entre 1 y 200 caracteres.");
        }

        var now = Now();
        var (version, error) = await BuildVersionAsync(draft, actor, now, cancellationToken);
        if (version is null)
        {
            return error!;
        }

        var campaign = new Campaign { Name = name, Status = CampaignStatus.Draft, StatusUtc = now, CreatedUtc = now, CreatedBy = actor };
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(cancellationToken);
        version.CampaignId = campaign.Id;
        version.Number = 1;
        db.CampaignVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        campaign.CurrentVersionId = version.Id;
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.CrearCampana, "Campana", Id(campaign.Id), null,
            new { nombre = name, version = AuditVersion(version) }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new CampaignResult(null, $"Campaña \"{name}\" creada (versión 1, borrador). Envíala a aprobación cuando esté lista.", campaign.Id);
    }

    /// <summary>
    /// Editar = versión nueva (la anterior no cambia). Si la anterior estaba aprobada, sus trabajos pendientes quedan
    /// Skipped VersionReemplazada y la campaña vuelve a Draft: la nueva exige aprobación.
    /// </summary>
    public async Task<CampaignResult> NewVersionAsync(string actor, long campaignId, CampaignDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        if (!CampaignRules.CanEdit(campaign.Status))
        {
            return WrongState(campaign, "editar");
        }

        var now = Now();
        var (version, error) = await BuildVersionAsync(draft, actor, now, cancellationToken);
        if (version is null)
        {
            return error!;
        }

        var previousId = campaign.CurrentVersionId;
        var previousStatus = campaign.Status;
        version.CampaignId = campaign.Id;
        version.Number = await db.CampaignVersions.Where(v => v.CampaignId == campaign.Id).MaxAsync(v => v.Number, cancellationToken) + 1;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.CampaignVersions.Add(version);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return CampaignResult.Fail(CampaignErrors.Conflicto, "Otra persona creó una versión al mismo tiempo. Recarga la campaña.");
        }

        if (!await MoveCampaignAsync(campaign.Id, previousStatus, previousId, CampaignStatus.Draft, version.Id, null, now, cancellationToken))
        {
            return CampaignResult.Fail(CampaignErrors.Conflicto, "La campaña cambió mientras la editabas. Recarga y vuelve a intentar.");
        }

        var replaced = previousId is null ? 0 : await SkipPendingAsync(previousId.Value, SkipReasons.VersionReemplazada, cancellationToken);
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.NuevaVersionCampana, "Campana", Id(campaign.Id),
            new { estado = previousStatus.ToString(), versionId = previousId },
            new { version = AuditVersion(version), trabajosReemplazados = replaced }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new CampaignResult(null,
            $"Versión {version.Number} creada (borrador)." + (replaced > 0 ? $" {replaced} envíos pendientes de la versión anterior no saldrán." : "")
            + " Necesita una aprobación nueva.", campaign.Id);
    }

    public async Task<CampaignResult> SubmitAsync(string actor, long campaignId, CancellationToken cancellationToken)
    {
        var (campaign, version) = await LoadAsync(campaignId, cancellationToken);
        if (campaign is null || version is null)
        {
            return NotFound();
        }

        if (!CampaignRules.CanSubmit(campaign.Status))
        {
            return WrongState(campaign, "enviar a aprobación");
        }

        if (await TemplateProblemAsync(version, cancellationToken) is { } problem)
        {
            return CampaignResult.Fail(CampaignErrors.Invalida, problem);
        }

        var now = Now();
        if (version.ExpiresUtc <= now)
        {
            return CampaignResult.Fail(CampaignErrors.Invalida, "La versión ya venció: crea una versión nueva con otras fechas.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MoveCampaignAsync(campaign.Id, CampaignStatus.Draft, version.Id, CampaignStatus.PendingApproval, version.Id, null, now, cancellationToken))
        {
            return CampaignResult.Fail(CampaignErrors.Conflicto, "La campaña cambió. Recarga y vuelve a intentar.");
        }

        await db.CampaignVersions.Where(v => v.Id == version.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.SubmittedUtc, now), cancellationToken);
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.EnviarAprobacionCampana, "Campana", Id(campaign.Id),
            null, new { version = version.Number, hash = version.Hash }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new CampaignResult(null, $"Versión {version.Number} enviada a aprobación. La debe aprobar otra persona con rol Aprobador.", campaign.Id);
    }

    public Task<CampaignResult> PauseAsync(string actor, long campaignId, string? reason, CancellationToken cancellationToken) =>
        ChangeAsync(actor, campaignId, CampaignRules.CanPause, CampaignStatus.Paused, reason, AuditActions.PausarCampana, "pausar",
            async (c, v) =>
            {
                // Lo pendiente queda OnHold: fuera del reclamo hasta reanudar.
                var held = await db.DispatchJobs
                    .Where(j => j.CampaignVersionId == v.Id
                                && (j.DispatchStatus == DispatchStatus.Queued || j.DispatchStatus == DispatchStatus.RetryScheduled))
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.DispatchStatus, DispatchStatus.OnHold), cancellationToken);
                return $"Campaña en pausa: {held} envíos esperan.";
            }, cancellationToken);

    public Task<CampaignResult> ResumeAsync(string actor, long campaignId, CancellationToken cancellationToken) =>
        ChangeAsync(actor, campaignId, CampaignRules.CanResume, CampaignStatus.Scheduled, null, AuditActions.ReanudarCampana, "reanudar",
            async (c, v) =>
            {
                if (v.ExpiresUtc <= Now())
                {
                    return null; // Vencida: la reanudación no tiene sentido (el ciclo de vida la pasa a Expired).
                }

                var queued = await db.DispatchJobs
                    .Where(j => j.CampaignVersionId == v.Id && j.DispatchStatus == DispatchStatus.OnHold)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.DispatchStatus,
                        j => j.NextAttemptUtc == null ? DispatchStatus.Queued : DispatchStatus.RetryScheduled), cancellationToken);
                return $"Campaña reanudada: {queued} envíos vuelven a la cola.";
            }, cancellationToken);

    public Task<CampaignResult> CancelAsync(string actor, long campaignId, string? reason, CancellationToken cancellationToken) =>
        ChangeAsync(actor, campaignId, CampaignRules.CanCancel, CampaignStatus.Cancelled, reason, AuditActions.CancelarCampana, "cancelar",
            async (c, _) =>
            {
                // Todas las versiones: nada pendiente de esta campaña sale.
                var versionIds = await db.CampaignVersions.Where(v => v.CampaignId == c.Id).Select(v => v.Id).ToListAsync(cancellationToken);
                var skipped = 0;
                foreach (var id in versionIds)
                {
                    skipped += await SkipPendingAsync(id, SkipReasons.CampanaCancelada, cancellationToken);
                }

                return $"Campaña cancelada: {skipped} envíos pendientes no saldrán.";
            }, cancellationToken);

    // ---------- Aprobador ----------

    /// <summary>
    /// Aprobación de cuatro ojos: quien creó la versión (o la campaña) no puede aprobarla. Se aprueba la versión que el
    /// aprobador vio (Id y Hash). En una transacción: campaña a Scheduled (UPDATE condicional), versión aprobada, un
    /// DispatchJob por destinatario (bajas revisadas al encolar) y AuditLog.
    /// </summary>
    public async Task<CampaignResult> ApproveAsync(string actor, long campaignId, long versionId, string? hash, CancellationToken cancellationToken)
    {
        var (campaign, version) = await LoadAsync(campaignId, cancellationToken);
        if (campaign is null || version is null)
        {
            return NotFound();
        }

        if (string.Equals(actor, version.CreatedBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals(actor, campaign.CreatedBy, StringComparison.OrdinalIgnoreCase))
        {
            LogFourEyes(logger, actor, campaignId);
            return CampaignResult.Fail(CampaignErrors.Prohibido,
                "No puedes aprobar una campaña que creaste o editaste: la aprueba otra persona (cuatro ojos).");
        }

        if (version.Id != versionId || (hash is not null && !string.Equals(hash, version.Hash, StringComparison.OrdinalIgnoreCase)))
        {
            return CampaignResult.Fail(CampaignErrors.Conflicto, "La campaña tiene una versión más nueva que la que viste. Recarga y revísala.");
        }

        if (!CampaignRules.CanDecide(campaign.Status))
        {
            return WrongState(campaign, "aprobar");
        }

        var now = Now();
        if (version.ExpiresUtc <= now)
        {
            return CampaignResult.Fail(CampaignErrors.Estado, "La versión ya venció: no se puede aprobar.");
        }

        if (await TemplateProblemAsync(version, cancellationToken) is { } problem)
        {
            return CampaignResult.Fail(CampaignErrors.Invalida, problem);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MoveCampaignAsync(campaign.Id, CampaignStatus.PendingApproval, version.Id, CampaignStatus.Scheduled, version.Id, null, now, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return CampaignResult.Fail(CampaignErrors.Conflicto, "La campaña ya fue aprobada, rechazada o editada por otra persona.");
        }

        await db.CampaignVersions.Where(v => v.Id == version.Id && v.ApprovedBy == null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ApprovedBy, actor).SetProperty(v => v.ApprovedUtc, now), cancellationToken);

        var jobs = await BuildJobsAsync(campaign.Id, version, cancellationToken);
        db.DispatchJobs.AddRange(jobs);
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.AprobarCampana, "Campana", Id(campaign.Id),
            new { estado = CampaignStatus.PendingApproval.ToString() },
            new
            {
                version = version.Number, hash = version.Hash, trabajos = jobs.Count,
                enCola = jobs.Count(j => j.DispatchStatus == DispatchStatus.Queued),
                omitidos = jobs.Where(j => j.DispatchStatus == DispatchStatus.Skipped).GroupBy(j => j.SkipReason)
                    .ToDictionary(g => g.Key ?? "", g => g.Count()),
            }, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Índice único (CampaignVersionId, ContactId): los trabajos ya existían. Nada de esta transacción queda.
            return CampaignResult.Fail(CampaignErrors.Conflicto, "Los envíos de esta versión ya estaban creados.");
        }

        var queued = jobs.Count(j => j.DispatchStatus == DispatchStatus.Queued);
        LogApproved(logger, campaign.Id, version.Number, actor, queued, jobs.Count - queued);
        return new CampaignResult(null,
            $"Versión {version.Number} aprobada: {queued} envíos en cola, {jobs.Count - queued} omitidos (sin consentimiento, baja o ya enviados).",
            campaign.Id);
    }

    public async Task<CampaignResult> RejectAsync(string actor, long campaignId, long versionId, string? reason, CancellationToken cancellationToken)
    {
        var (campaign, version) = await LoadAsync(campaignId, cancellationToken);
        if (campaign is null || version is null)
        {
            return NotFound();
        }

        var text = reason?.Trim() ?? "";
        if (text.Length is 0 or > 1000)
        {
            return CampaignResult.Fail(CampaignErrors.Invalida, "Escribe el motivo del rechazo (hasta 1000 caracteres).");
        }

        if (version.Id != versionId)
        {
            return CampaignResult.Fail(CampaignErrors.Conflicto, "La campaña tiene una versión más nueva que la que viste. Recarga y revísala.");
        }

        if (!CampaignRules.CanDecide(campaign.Status))
        {
            return WrongState(campaign, "rechazar");
        }

        var now = Now();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MoveCampaignAsync(campaign.Id, CampaignStatus.PendingApproval, version.Id, CampaignStatus.Draft, version.Id, text, now, cancellationToken))
        {
            return CampaignResult.Fail(CampaignErrors.Conflicto, "La campaña ya fue aprobada, rechazada o editada por otra persona.");
        }

        await db.CampaignVersions.Where(v => v.Id == version.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.RejectedBy, actor).SetProperty(v => v.RejectedUtc, now)
                .SetProperty(v => v.RejectionReason, text), cancellationToken);
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.RechazarCampana, "Campana", Id(campaign.Id), null,
            new { version = version.Number, motivo = text }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new CampaignResult(null, $"Versión {version.Number} rechazada: vuelve a borrador para que el operador la corrija.", campaign.Id);
    }

    // ---------- Apoyo ----------

    /// <summary>Arma una versión (sin Id ni número) validando plantilla, lista, parámetros y fechas.</summary>
    private async Task<(CampaignVersion? Version, CampaignResult? Error)> BuildVersionAsync(
        CampaignDraft draft, string actor, DateTime now, CancellationToken cancellationToken)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == draft.TemplateId, cancellationToken);
        if (template is null)
        {
            return (null, CampaignResult.Fail(CampaignErrors.Invalida, "La plantilla no existe."));
        }

        if (template.Category != TemplateCategories.Marketing)
        {
            // Regla 12 / D-009: en esta etapa solo promociones.
            return (null, CampaignResult.Fail(CampaignErrors.Invalida, $"La plantilla {template.Name} es {template.Category}: solo se envían plantillas de marketing."));
        }

        if (!await db.Segments.AnyAsync(s => s.Id == draft.SegmentId, cancellationToken))
        {
            return (null, CampaignResult.Fail(CampaignErrors.Invalida, "La lista no existe."));
        }

        // Audiencia congelada: miembros con teléfono y activos en este momento.
        var members = await (from m in db.SegmentMembers.AsNoTracking()
                             join c in db.Contacts.AsNoTracking() on m.ContactId equals c.Id
                             where m.SegmentId == draft.SegmentId
                             select new { c.Id, c.PhoneE164, c.IsActive }).ToListAsync(cancellationToken);
        var audience = members.Where(m => m.PhoneE164 != null && m.IsActive).Select(m => m.Id).ToList();

        var parameters = (draft.Parameters ?? []).Select(p => p with { Name = p.Name.Trim(), Value = p.Value?.Trim() ?? "" }).ToList();
        var content = TemplateContent.Parse(template.Components);
        var errors = CampaignRules.Validate(content.BodyParameters, parameters, draft.ScheduledUtc, draft.ExpiresUtc, now, audience.Count);
        if (errors.Count > 0)
        {
            return (null, CampaignResult.Fail(CampaignErrors.Invalida, string.Join(" ", errors)));
        }

        // Los parámetros se guardan en el orden del cuerpo de la plantilla (así se envían los posicionales).
        var ordered = content.BodyParameters.Select(n => parameters.Single(p => p.Name == n)).ToList();
        var version = new CampaignVersion
        {
            TemplateId = template.Id,
            TemplateName = template.Name,
            Language = template.Language,
            Category = template.Category,
            ParameterFormat = template.ParameterFormat ?? (TemplateRules.IsPositional(content.BodyParameters) ? "POSITIONAL" : "NAMED"),
            SegmentId = draft.SegmentId,
            Audience = CampaignRules.SerializeAudience(audience),
            AudienceCount = audience.Count,
            ExcludedCount = members.Count - audience.Count,
            Parameters = CampaignRules.SerializeParameters(ordered),
            ScheduledUtc = DateTime.SpecifyKind(draft.ScheduledUtc, DateTimeKind.Utc),
            ExpiresUtc = DateTime.SpecifyKind(draft.ExpiresUtc, DateTimeKind.Utc),
            Hash = "",
            CreatedUtc = now,
            CreatedBy = actor,
        };
        version.Hash = CampaignRules.ComputeHash(version);
        return (version, null);
    }

    /// <summary>Un DispatchJob por contacto de la audiencia congelada, con sus parámetros y las bajas revisadas al encolar.</summary>
    private async Task<List<DispatchJob>> BuildJobsAsync(long campaignId, CampaignVersion version, CancellationToken cancellationToken)
    {
        var ids = CampaignRules.DeserializeAudience(version.Audience).ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.PhoneE164, c.Name, c.IsActive }).ToListAsync(cancellationToken);
        var otherVersions = await db.CampaignVersions.Where(v => v.CampaignId == campaignId && v.Id != version.Id)
            .Select(v => v.Id).ToListAsync(cancellationToken);
        var alreadySent = (await db.DispatchJobs.AsNoTracking()
            .Where(j => j.CampaignVersionId != null && otherVersions.Contains(j.CampaignVersionId.Value) && ids.Contains(j.ContactId)
                        && (j.DispatchStatus == DispatchStatus.Submitted || j.DispatchStatus == DispatchStatus.NeedsReview
                            || j.DispatchStatus == DispatchStatus.Claimed))
            .Select(j => j.ContactId).ToListAsync(cancellationToken)).ToHashSet();
        var parameters = CampaignRules.DeserializeParameters(version.Parameters);

        var jobs = new List<DispatchJob>();
        foreach (var c in contacts.Where(c => c.PhoneE164 is not null).OrderBy(c => c.Id))
        {
            string? reason = alreadySent.Contains(c.Id) ? SkipReasons.YaEnviado
                : !c.IsActive ? SkipReasons.ContactoInactivo
                : (await consent.EvaluateAsync(c.PhoneE164!, cancellationToken)).SkipReason;
            jobs.Add(new DispatchJob
            {
                CampaignVersionId = version.Id,
                ContactId = c.Id,
                PhoneE164 = c.PhoneE164!,
                Parameters = CampaignRules.SerializeJobParameters(CampaignRules.Resolve(parameters, c.Name)),
                ScheduledUtc = version.ScheduledUtc,
                ExpiresUtc = version.ExpiresUtc,
                DispatchStatus = reason is null ? DispatchStatus.Queued : DispatchStatus.Skipped,
                SkipReason = reason,
            });
        }

        return jobs;
    }

    /// <summary>Plantilla de la versión aprobada en Meta y de marketing; si no, el motivo.</summary>
    private async Task<string?> TemplateProblemAsync(CampaignVersion version, CancellationToken cancellationToken)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == version.TemplateId, cancellationToken);
        return template switch
        {
            null => "La plantilla de la versión ya no existe.",
            { Status: not TemplateStatuses.Approved } => $"La plantilla {template.Name} está {template.Status}: debe estar APPROVED en Meta.",
            { Category: not TemplateCategories.Marketing } => $"Meta recategorizó {template.Name} como {template.Category}: solo se envían promociones de marketing.",
            _ => null,
        };
    }

    /// <summary>Cambio de estado con verificación optimista: solo si la campaña sigue en <paramref name="from"/> con la versión esperada.</summary>
    private async Task<bool> MoveCampaignAsync(
        long id, CampaignStatus from, long? expectedVersion, CampaignStatus to, long? newVersion, string? reason, DateTime now,
        CancellationToken cancellationToken) =>
        await db.Campaigns
            .Where(c => c.Id == id && c.Status == from && c.CurrentVersionId == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, to)
                .SetProperty(c => c.CurrentVersionId, newVersion)
                .SetProperty(c => c.StatusReason, reason)
                .SetProperty(c => c.StatusUtc, now), cancellationToken) == 1;

    private Task<int> SkipPendingAsync(long versionId, string reason, CancellationToken cancellationToken) =>
        db.DispatchJobs
            .Where(j => j.CampaignVersionId == versionId && Pending.Contains(j.DispatchStatus))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.DispatchStatus, DispatchStatus.Skipped).SetProperty(j => j.SkipReason, reason),
                cancellationToken);

    private async Task<CampaignResult> ChangeAsync(
        string actor, long campaignId, Func<CampaignStatus, bool> allowed, CampaignStatus to, string? reason, string auditAction,
        string verb, Func<Campaign, CampaignVersion, Task<string?>> jobs, CancellationToken cancellationToken)
    {
        var (campaign, version) = await LoadAsync(campaignId, cancellationToken);
        if (campaign is null || version is null)
        {
            return NotFound();
        }

        if (!allowed(campaign.Status))
        {
            return WrongState(campaign, verb);
        }

        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 1000)];
        var now = Now();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MoveCampaignAsync(campaign.Id, campaign.Status, campaign.CurrentVersionId, to, campaign.CurrentVersionId, text, now, cancellationToken))
        {
            return CampaignResult.Fail(CampaignErrors.Conflicto, "La campaña cambió. Recarga y vuelve a intentar.");
        }

        var message = await jobs(campaign, version);
        if (message is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return CampaignResult.Fail(CampaignErrors.Estado, $"No se puede {verb}: la versión ya venció.");
        }

        db.AuditLogs.Add(AuditEntry.Create(actor, auditAction, "Campana", Id(campaign.Id),
            new { estado = campaign.Status.ToString() }, new { estado = to.ToString(), motivo = text, detalle = message }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new CampaignResult(null, message, campaign.Id);
    }

    private async Task<(Campaign? Campaign, CampaignVersion? Version)> LoadAsync(long campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == campaignId, cancellationToken);
        var version = campaign?.CurrentVersionId is { } v
            ? await db.CampaignVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == v, cancellationToken)
            : null;
        return (campaign, version);
    }

    private static CampaignResult NotFound() => CampaignResult.Fail(CampaignErrors.NoExiste, "La campaña no existe.");

    private static CampaignResult WrongState(Campaign c, string verb) =>
        CampaignResult.Fail(CampaignErrors.Estado, $"No se puede {verb} una campaña en estado {c.Status}.");

    private static object AuditVersion(CampaignVersion v) => new
    {
        numero = v.Number, plantilla = v.TemplateName, idioma = v.Language, lista = v.SegmentId, destinatarios = v.AudienceCount,
        parametros = CampaignRules.DeserializeParameters(v.Parameters), programada = v.ScheduledUtc, expira = v.ExpiresUtc, hash = v.Hash,
    };

    private static CampaignSummary ToSummary(Campaign c, CampaignVersion? v) =>
        new(c.Id, c.Name, c.Status, c.StatusUtc, c.StatusReason, v?.Number, v?.TemplateName, v?.Language, v?.AudienceCount ?? 0,
            v?.ScheduledUtc, v?.ExpiresUtc, c.CreatedBy, c.CreatedUtc);

    private static VersionDetail ToDetail(CampaignVersion v, string? segmentName) =>
        new(v.Id, v.Number, v.TemplateId, v.TemplateName, v.Language, v.Category, v.ParameterFormat, v.SegmentId, segmentName,
            v.AudienceCount, v.ExcludedCount, CampaignRules.DeserializeParameters(v.Parameters), v.ScheduledUtc, v.ExpiresUtc, v.Hash,
            v.CreatedUtc, v.CreatedBy, v.SubmittedUtc, v.ApprovedUtc, v.ApprovedBy, v.RejectedUtc, v.RejectedBy, v.RejectionReason);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Actor} intentó aprobar la campaña {CampaignId}, que creó o editó (cuatro ojos)")]
    private static partial void LogFourEyes(ILogger logger, string actor, long campaignId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Campaña {CampaignId} v{Version} aprobada por {Actor}: {Queued} en cola, {Skipped} omitidos")]
    private static partial void LogApproved(ILogger logger, long campaignId, int version, string actor, int queued, int skipped);
}
