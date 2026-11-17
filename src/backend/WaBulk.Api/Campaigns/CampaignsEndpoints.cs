using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WaBulk.Api.Users;
using WaBulk.Domain.Campaigns;
using WaBulk.Domain.Templates;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Campaigns;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Infrastructure.Templates;

namespace WaBulk.Api.Campaigns;

public sealed record ParametroDto(string Nombre, string Fuente, string Valor);

public sealed record CampanaRequest(
    string? Nombre, long PlantillaId, long ListaId, IReadOnlyList<ParametroDto>? Parametros, DateTime ProgramadaUtc, DateTime ExpiraUtc);

public sealed record MotivoRequest(string? Motivo);

public sealed record DecisionRequest(long VersionId, string? Hash, string? Motivo);

public sealed record ResultadoDto(string Mensaje, long? Id);

public sealed record CampanaDto(
    long Id, string Nombre, string Estado, DateTime EstadoUtc, string? Motivo, int? Version, string? Plantilla, string? Idioma,
    int Destinatarios, DateTime? ProgramadaUtc, DateTime? ExpiraUtc, string CreadaPor, DateTime CreadaUtc);

public sealed record VersionDto(
    long Id, int Numero, long PlantillaId, string Plantilla, string Idioma, string Categoria, string FormatoParametros,
    long? ListaId, string? Lista, int Destinatarios, int Excluidos, IReadOnlyList<ParametroDto> Parametros, DateTime ProgramadaUtc,
    DateTime ExpiraUtc, string Hash, DateTime CreadaUtc, string CreadaPor, DateTime? EnviadaAprobacionUtc, DateTime? AprobadaUtc,
    string? AprobadaPor, DateTime? RechazadaUtc, string? RechazadaPor, string? MotivoRechazo);

public sealed record ConteoDto(string Estado, string? Motivo, int Cantidad);

public sealed record CampanaDetalleDto(
    CampanaDto Campana, IReadOnlyList<VersionDto> Versiones, IReadOnlyList<ConteoDto> Envios, string? Encabezado, string? Cuerpo,
    string? Pie, IReadOnlyList<string> Botones);

public sealed record PlantillaOpcionDto(long Id, string Nombre, string Idioma, string Estado, IReadOnlyList<string> Parametros);

public sealed record ListaOpcionDto(long Id, string Nombre, int Miembros);

public sealed record OpcionesDto(IReadOnlyList<PlantillaOpcionDto> Plantillas, IReadOnlyList<ListaOpcionDto> Listas);

/// <summary>
/// Campañas (F1.6). El Operador (PuedeEnviar) las crea, edita (versión nueva), envía a aprobación, pausa, reanuda y
/// cancela en /api/campanias; el Aprobador (PuedeAprobar) las aprueba o rechaza en /api/aprobaciones. Separar las rutas
/// mantiene las políticas 1:1 con los roles (D-030). La regla de cuatro ojos se valida en CampaignService (403).
/// </summary>
public static class CampaignsEndpoints
{
    public static RouteGroupBuilder MapCampaignsEndpoints(this RouteGroupBuilder api)
    {
        var campaigns = api.MapGroup("/campanias").RequireAuthorization(Policies.PuedeEnviar);

        campaigns.MapGet("", async (string? estado, CampaignService service, CancellationToken ct) =>
            Enum.TryParse<CampaignStatus>(estado, out var s) || string.IsNullOrEmpty(estado)
                ? Results.Ok((await service.ListAsync(string.IsNullOrEmpty(estado) ? null : s, ct)).Select(ToDto))
                : Results.BadRequest(new ErrorDto("EstadoInvalido", $"Estado desconocido: {estado}.")));

        campaigns.MapGet("/opciones", async (WaBulkDbContext db, CancellationToken ct) =>
        {
            // Solo marketing (D-009). Se listan también las no aprobadas para poder preparar el borrador.
            var templates = await db.Templates.AsNoTracking()
                .Where(t => t.Category == TemplateCategories.Marketing && t.MetaTemplateId != null)
                .OrderBy(t => t.Name).ToListAsync(ct);
            var lists = await db.Segments.AsNoTracking().OrderBy(s => s.Name)
                .Select(s => new ListaOpcionDto(s.Id, s.Name, db.SegmentMembers.Count(m => m.SegmentId == s.Id))).ToListAsync(ct);
            return Results.Ok(new OpcionesDto(
                [.. templates.Select(t => new PlantillaOpcionDto(t.Id, t.Name, t.Language, t.Status, TemplateContent.Parse(t.Components).BodyParameters))],
                lists));
        });

        campaigns.MapGet("/{id:long}", async (long id, CampaignService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } d ? Results.Ok(ToDto(d)) : NotFound());

        campaigns.MapPost("", async (CampanaRequest r, ClaimsPrincipal me, CampaignService service, CancellationToken ct) =>
            ToResult(await service.CreateAsync(Actor(me), ToDraft(r), ct)));

        campaigns.MapPost("/{id:long}/versiones", async (long id, CampanaRequest r, ClaimsPrincipal me, CampaignService service,
            CancellationToken ct) => ToResult(await service.NewVersionAsync(Actor(me), id, ToDraft(r), ct)));

        campaigns.MapPost("/{id:long}/enviar", async (long id, ClaimsPrincipal me, CampaignService service, CancellationToken ct) =>
            ToResult(await service.SubmitAsync(Actor(me), id, ct)));

        campaigns.MapPost("/{id:long}/pausar", async (long id, MotivoRequest r, ClaimsPrincipal me, CampaignService service,
            CancellationToken ct) => ToResult(await service.PauseAsync(Actor(me), id, r.Motivo, ct)));

        campaigns.MapPost("/{id:long}/reanudar", async (long id, ClaimsPrincipal me, CampaignService service, CancellationToken ct) =>
            ToResult(await service.ResumeAsync(Actor(me), id, ct)));

        campaigns.MapPost("/{id:long}/cancelar", async (long id, MotivoRequest r, ClaimsPrincipal me, CampaignService service,
            CancellationToken ct) => ToResult(await service.CancelAsync(Actor(me), id, r.Motivo, ct)));

        var approvals = api.MapGroup("/aprobaciones").RequireAuthorization(Policies.PuedeAprobar);

        approvals.MapGet("", async (CampaignService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(CampaignStatus.PendingApproval, ct)).Select(ToDto)));

        approvals.MapGet("/{id:long}", async (long id, CampaignService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } d ? Results.Ok(ToDto(d)) : NotFound());

        approvals.MapPost("/{id:long}/aprobar", async (long id, DecisionRequest r, ClaimsPrincipal me, CampaignService service,
            CancellationToken ct) => ToResult(await service.ApproveAsync(Actor(me), id, r.VersionId, r.Hash, ct)));

        approvals.MapPost("/{id:long}/rechazar", async (long id, DecisionRequest r, ClaimsPrincipal me, CampaignService service,
            CancellationToken ct) => ToResult(await service.RejectAsync(Actor(me), id, r.VersionId, r.Motivo, ct)));

        return api;
    }

    private static IResult NotFound() => Results.NotFound(new ErrorDto(CampaignErrors.NoExiste, "La campaña no existe."));

    private static IResult ToResult(CampaignResult r) => r.Error switch
    {
        null => Results.Ok(new ResultadoDto(r.Message, r.CampaignId)),
        CampaignErrors.Prohibido => Results.Json(new ErrorDto(r.Error, r.Message), statusCode: StatusCodes.Status403Forbidden),
        CampaignErrors.NoExiste => Results.NotFound(new ErrorDto(r.Error, r.Message)),
        CampaignErrors.Conflicto => Results.Conflict(new ErrorDto(r.Error, r.Message)),
        _ => Results.BadRequest(new ErrorDto(r.Error, r.Message)),
    };

    private static CampaignDraft ToDraft(CampanaRequest r) => new(
        r.Nombre, r.PlantillaId, r.ListaId,
        [.. (r.Parametros ?? []).Select(p => new CampaignParameter(p.Nombre ?? "",
            Enum.TryParse<ParameterSource>(p.Fuente, out var f) ? f : ParameterSource.Fijo, p.Valor ?? ""))],
        DateTime.SpecifyKind(r.ProgramadaUtc.ToUniversalTime(), DateTimeKind.Utc),
        DateTime.SpecifyKind(r.ExpiraUtc.ToUniversalTime(), DateTimeKind.Utc));

    private static string Actor(ClaimsPrincipal me) => me.Identity?.Name ?? "desconocido";

    private static CampanaDto ToDto(CampaignSummary c) =>
        new(c.Id, c.Name, c.Status.ToString(), c.StatusUtc, c.StatusReason, c.VersionNumber, c.TemplateName, c.Language, c.AudienceCount,
            c.ScheduledUtc, c.ExpiresUtc, c.CreatedBy, c.CreatedUtc);

    private static CampanaDetalleDto ToDto(CampaignDetail d) =>
        new(ToDto(d.Campaign),
            [.. d.Versions.Select(v => new VersionDto(v.Id, v.Number, v.TemplateId, v.TemplateName, v.Language, v.Category, v.ParameterFormat,
                v.SegmentId, v.SegmentName, v.AudienceCount, v.ExcludedCount,
                [.. v.Parameters.Select(p => new ParametroDto(p.Name, p.Source.ToString(), p.Value))],
                v.ScheduledUtc, v.ExpiresUtc, v.Hash, v.CreatedUtc, v.CreatedBy, v.SubmittedUtc, v.ApprovedUtc, v.ApprovedBy,
                v.RejectedUtc, v.RejectedBy, v.RejectionReason))],
            [.. d.Jobs.Select(j => new ConteoDto(j.Status, j.Reason, j.Count))],
            d.Content?.Header, d.Content?.Body, d.Content?.Footer, d.Content?.Buttons ?? []);
}
