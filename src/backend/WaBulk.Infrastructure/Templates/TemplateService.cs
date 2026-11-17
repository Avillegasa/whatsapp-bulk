using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Templates;

/// <summary>Sección "Plantillas": texto del botón de interés (D-012). El de baja es el primero de Bajas:ButtonTexts.</summary>
public sealed class TemplateOptions
{
    public const string SectionName = "Plantillas";

    public string InterestButton { get; set; } = "Me interesa";
}

public sealed record TemplateSummary(
    long Id, string? MetaTemplateId, string Name, string Language, string Category, string Status, DateTime StatusUtc,
    string? StatusReason, string? LastEvent, string? Quality, string? CorrectCategory, DateTime? CategoryChangeUtc,
    DateTime? SyncedUtc, string CreatedBy);

public sealed record TemplateDetail(TemplateSummary Template, string? ParameterFormat, TemplateContent Content);

public sealed record TemplateSyncResult(bool Succeeded, string? Error, int Total, int Added, int Updated, int Missing);

public sealed record TemplateCreateResult(string? Error, string Message, TemplateSummary? Template)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Plantillas (F1.5): lista, sincronización con Meta y creación de plantillas de marketing. Cada acción de un usuario se
/// audita en la misma transacción que el cambio (D-030). La creación sigue el mismo patrón que SendAttempt (regla 3):
/// la fila y su AuditLog se confirman ANTES del único POST; sin respuesta confiable queda INCIERTA y la sincronización
/// la resuelve por nombre e idioma, sin reintento automático (regla 4).
/// </summary>
public sealed partial class TemplateService(
    WaBulkDbContext db,
    MetaTemplateApiClient meta,
    IOptions<TemplateOptions> options,
    IOptions<OptOutOptions> optOut,
    TimeProvider clock,
    ILogger<TemplateService> logger)
{
    /// <summary>Nombre del actor de las filas que llegan de Meta (sincronización o webhook).</summary>
    public const string MetaActor = "meta";

    public string InterestButton => options.Value.InterestButton;

    /// <summary>El botón de baja es el texto que reconoce OptOutDetector: así un toque siempre se registra como baja.</summary>
    public string OptOutButton => optOut.Value.EffectiveButtonTexts[0];

    public Task<List<TemplateSummary>> ListAsync(CancellationToken cancellationToken) =>
        db.Templates.AsNoTracking().OrderBy(t => t.Name).ThenBy(t => t.Language)
            .Select(t => new TemplateSummary(t.Id, t.MetaTemplateId, t.Name, t.Language, t.Category, t.Status, t.StatusUtc,
                t.StatusReason, t.LastEvent, t.Quality, t.CorrectCategory, t.CategoryChangeUtc, t.SyncedUtc, t.CreatedBy))
            .ToListAsync(cancellationToken);

    public async Task<TemplateDetail?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var t = await db.Templates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return t is null ? null : new TemplateDetail(ToSummary(t), t.ParameterFormat, TemplateContent.Parse(t.Components));
    }

    /// <summary>
    /// Trae todas las plantillas de la WABA y actualiza la tabla: agrega las nuevas, actualiza estado, calidad, categoría
    /// y componentes, resuelve las INCIERTA por nombre e idioma y marca NO_ENCONTRADA las que ya no están en Meta.
    /// </summary>
    public async Task<TemplateSyncResult> SyncAsync(string actor, CancellationToken cancellationToken)
    {
        IReadOnlyList<MetaTemplate> remote;
        try
        {
            remote = await meta.ListAsync(cancellationToken);
        }
        catch (MetaApiException ex)
        {
            LogSyncFailed(logger, ex.Message);
            return new TemplateSyncResult(false, ex.Message, 0, 0, 0, 0);
        }

        var now = Now();
        var local = await db.Templates.ToListAsync(cancellationToken);
        int added = 0, updated = 0, missing = 0;
        var seen = new HashSet<long>();
        foreach (var r in remote)
        {
            var language = TemplateRules.NormalizeLanguage(r.Language);
            var t = local.FirstOrDefault(x => x.MetaTemplateId == r.Id)
                    ?? local.FirstOrDefault(x => x.MetaTemplateId is null && x.Name == r.Name && x.Language == language);
            if (t is null)
            {
                t = new Template
                {
                    Name = r.Name, Language = language, Category = r.Category, Status = r.Status, CreatedUtc = now,
                    CreatedBy = MetaActor,
                };
                db.Templates.Add(t);
                local.Add(t);
                added++;
            }
            else
            {
                updated++;
            }

            t.MetaTemplateId = r.Id;
            t.Name = r.Name;
            t.Language = language;
            t.Category = r.Category;
            t.Status = r.Status;
            t.StatusUtc = now;
            t.StatusReason = r.RejectedReason is null or "NONE" ? null : r.RejectedReason;
            if (r.QualityScore is not null && r.QualityScore != t.Quality)
            {
                t.Quality = r.QualityScore;
                t.QualityUtc = now;
            }

            t.PreviousCategory = r.PreviousCategory ?? t.PreviousCategory;
            t.CorrectCategory = r.CorrectCategory;
            t.CategoryChangeUtc = r.CorrectCategory is null ? null : t.CategoryChangeUtc;
            t.ParameterFormat = r.ParameterFormat;
            t.Components = r.ComponentsJson;
            t.SyncedUtc = now;
            seen.Add(t.Id); // Las nuevas tienen Id 0 hasta guardar: no cuentan como faltantes.
        }

        foreach (var t in local.Where(x => x.Id != 0 && !seen.Contains(x.Id) && x.MetaTemplateId is not null
                                           && x.Status != TemplateStatuses.NotFound))
        {
            t.Status = TemplateStatuses.NotFound;
            t.StatusUtc = now;
            t.StatusReason = "La sincronización no la encontró en la WABA (borrada o de otra cuenta).";
            missing++;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.SincronizarPlantillas, "Plantilla", null, null,
            new { total = remote.Count, nuevas = added, actualizadas = updated, noEncontradas = missing }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        LogSynced(logger, remote.Count, added, updated, missing);
        return new TemplateSyncResult(true, null, remote.Count, added, updated, missing);
    }

    /// <summary>Crea una plantilla de marketing en Meta (D-009) con los botones «Me interesa» y «No recibir más» (D-012).</summary>
    public async Task<TemplateCreateResult> CreateAsync(string actor, TemplateDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var errors = TemplateRules.Validate(draft, InterestButton, OptOutButton);
        if (errors.Count > 0)
        {
            return new TemplateCreateResult("Invalida", string.Join(" ", errors), null);
        }

        var name = draft.Name!.Trim();
        var language = draft.Language!.Trim();
        var request = TemplateContent.BuildCreateRequest(draft, InterestButton, OptOutButton);
        var componentsJson = request["components"]!.ToJsonString();
        var now = Now();

        var t = await db.Templates.SingleOrDefaultAsync(x => x.Name == name && x.Language == language, cancellationToken);
        // CREANDO de hace más de 5 min: el proceso se cortó entre el commit y el resultado del POST.
        var stale = t is { Status: TemplateStatuses.Creating } && t.StatusUtc < now.AddMinutes(-5);
        if (t is not null && !stale && !TemplateStatuses.CanRetryCreate(t.Status))
        {
            return new TemplateCreateResult("Repetida",
                $"Ya existe la plantilla {name} ({language}) con estado {t.Status}. Usa otro nombre.", null);
        }

        var before = t is null ? null : new { estado = t.Status, motivo = t.StatusReason };
        if (t is null)
        {
            t = new Template { Name = name, Language = language, Category = TemplateCategories.Marketing, Status = TemplateStatuses.Creating, CreatedUtc = now, CreatedBy = actor };
            db.Templates.Add(t);
        }

        t.MetaTemplateId = null;
        t.Category = TemplateCategories.Marketing;
        t.Status = TemplateStatuses.Creating;
        t.StatusUtc = now;
        t.StatusReason = null;
        t.ParameterFormat = "NAMED";
        t.Components = componentsJson;

        // 1) Fila y auditoría confirmadas ANTES del POST (si dos personas crean lo mismo, el índice único corta a una).
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.CrearPlantilla, "Plantilla",
                t.Id.ToString(CultureInfo.InvariantCulture), before,
                new { nombre = name, idioma = language, categoria = t.Category, componentes = request["components"] }, now));
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new TemplateCreateResult("Repetida", $"Otra persona está creando {name} ({language}) en este momento.", null);
        }

        // 2) Un solo POST, sin reintento.
        var result = await meta.CreateAsync(request, cancellationToken);

        // 3) Resultado guardado aunque se haya pedido cancelar: el POST ya ocurrió.
        t.StatusUtc = Now();
        string message;
        switch (result.Outcome)
        {
            case MetaCreateOutcome.Created:
                t.MetaTemplateId = result.Id;
                t.Status = result.Status ?? TemplateStatuses.Pending;
                t.Category = result.Category ?? t.Category;
                message = $"Plantilla {name} enviada a Meta: estado {t.Status}. La aprobación llega por webhook (suele tardar minutos).";
                break;
            case MetaCreateOutcome.Rejected:
                t.Status = TemplateStatuses.CreateFailed;
                t.StatusReason = $"Código {result.ErrorCode}/{result.ErrorSubcode}: {result.Detail}";
                message = $"Meta no aceptó la plantilla: {result.Detail}";
                break;
            default:
                t.Status = TemplateStatuses.Unknown;
                t.StatusReason = result.Detail;
                message = "Meta no respondió con certeza. No se reintenta: sincroniza en unos minutos para ver si se creó.";
                break;
        }

        await db.SaveChangesAsync(CancellationToken.None);
        LogCreated(logger, name, language, t.Status);
        return new TemplateCreateResult(result.Outcome == MetaCreateOutcome.Rejected ? "RechazadaPorMeta" : null, message, ToSummary(t));
    }

    private static TemplateSummary ToSummary(Template t) =>
        new(t.Id, t.MetaTemplateId, t.Name, t.Language, t.Category, t.Status, t.StatusUtc, t.StatusReason, t.LastEvent, t.Quality,
            t.CorrectCategory, t.CategoryChangeUtc, t.SyncedUtc, t.CreatedBy);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sincronización de plantillas fallida: {Error}")]
    private static partial void LogSyncFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plantillas sincronizadas: {Total} en Meta, {Added} nuevas, {Updated} actualizadas, {Missing} no encontradas")]
    private static partial void LogSynced(ILogger logger, int total, int added, int updated, int missing);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plantilla {Name} ({Language}) creada en Meta: {Status}")]
    private static partial void LogCreated(ILogger logger, string name, string language, string status);
}
