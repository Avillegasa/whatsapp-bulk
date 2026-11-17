using System.Security.Claims;
using WaBulk.Api.Users;
using WaBulk.Domain.Templates;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Templates;

namespace WaBulk.Api.Templates;

public sealed record PlantillaDto(
    long Id, string? MetaId, string Nombre, string Idioma, string Categoria, string Estado, DateTime EstadoUtc, string? Motivo,
    string? UltimoEvento, string? Calidad, string? CategoriaNueva, DateTime? CambioCategoriaUtc, DateTime? SincronizadaUtc,
    string CreadaPor);

public sealed record PlantillaDetalleDto(
    PlantillaDto Plantilla, string? FormatoParametros, string? Encabezado, string? Cuerpo, string? Pie,
    IReadOnlyList<string> Botones, IReadOnlyList<string> Parametros);

public sealed record CrearPlantillaRequest(
    string? Nombre, string? Idioma, string? Encabezado, string? Cuerpo, string? Pie, Dictionary<string, string>? Ejemplos);

public sealed record CrearPlantillaDto(string Mensaje, PlantillaDto? Plantilla);

public sealed record SincronizacionDto(int Total, int Nuevas, int Actualizadas, int NoEncontradas);

public sealed record BotonesDto(string Interes, string Baja);

/// <summary>
/// Plantillas (F1.5) con la política PuedeEnviar: las arma el mismo Operador que luego crea las campañas; la aprobación
/// de cuatro ojos está en la campaña (F1.6). La lógica y la auditoría están en TemplateService.
/// </summary>
public static class TemplatesEndpoints
{
    public static RouteGroupBuilder MapTemplatesEndpoints(this RouteGroupBuilder api)
    {
        var templates = api.MapGroup("/plantillas").RequireAuthorization(Policies.PuedeEnviar);

        templates.MapGet("", async (TemplateService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToDto)));

        templates.MapGet("/botones", (TemplateService service) =>
            Results.Ok(new BotonesDto(service.InterestButton, service.OptOutButton)));

        templates.MapGet("/{id:long}", async (long id, TemplateService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } d
                ? Results.Ok(new PlantillaDetalleDto(ToDto(d.Template), d.ParameterFormat, d.Content.Header, d.Content.Body,
                    d.Content.Footer, d.Content.Buttons, d.Content.BodyParameters))
                : Results.NotFound(new ErrorDto("NoExiste", "La plantilla no existe.")));

        templates.MapPost("/sincronizar", async (ClaimsPrincipal me, TemplateService service, CancellationToken ct) =>
        {
            var r = await service.SyncAsync(Actor(me), ct);
            return r.Succeeded
                ? Results.Ok(new SincronizacionDto(r.Total, r.Added, r.Updated, r.Missing))
                : Results.Json(new ErrorDto("MetaNoDisponible", r.Error ?? "Meta no respondió."), statusCode: StatusCodes.Status502BadGateway);
        });

        templates.MapPost("", async (CrearPlantillaRequest request, ClaimsPrincipal me, TemplateService service, CancellationToken ct) =>
        {
            var draft = new TemplateDraft(request.Nombre, request.Idioma, request.Encabezado, request.Cuerpo, request.Pie, request.Ejemplos);
            var r = await service.CreateAsync(Actor(me), draft, ct);
            return r.Error switch
            {
                null => Results.Ok(new CrearPlantillaDto(r.Message, r.Template is null ? null : ToDto(r.Template))),
                // Meta la rechazó: la fila quedó ERROR_CREACION con el motivo; se informa como error de validación.
                _ => Results.BadRequest(new ErrorDto(r.Error, r.Message)),
            };
        });

        return api;
    }

    private static string Actor(ClaimsPrincipal me) => me.Identity?.Name ?? "desconocido";

    private static PlantillaDto ToDto(TemplateSummary t) =>
        new(t.Id, t.MetaTemplateId, t.Name, t.Language, t.Category, t.Status, t.StatusUtc, t.StatusReason, t.LastEvent, t.Quality,
            t.CorrectCategory, t.CategoryChangeUtc, t.SyncedUtc, t.CreatedBy);
}
