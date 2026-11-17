using System.Security.Claims;
using WaBulk.Api.Users;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Contacts;

namespace WaBulk.Api.Contacts;

public sealed record ContactoDto(
    long Id, string? Telefono, string? Nombre, string? ClienteId, bool Activo, string Consentimiento, bool BajaPlataforma,
    DateTime? UltimoMensajeUtc);

public sealed record ContactoDetalleDto(
    ContactoDto Contacto, string? UserId, IReadOnlyList<ConsentHistoryItem> Historial, IReadOnlyList<InboundItem> Mensajes,
    IReadOnlyList<OfferItem> Ofertas, IReadOnlyList<string> Listas);

public sealed record ConversacionDto(
    long ContactoId, string? Telefono, string? Nombre, string Tipo, string? Texto, DateTime FechaUtc, DateTime VentanaHastaUtc,
    string Consentimiento, bool PuedeOfrecer, string? Motivo, string? UltimaOferta, string? Respuesta);

public sealed record OfertaDto(bool Enviada, string Mensaje, long? OfertaId, string? Estado);

public sealed record BajaRequest(string? Motivo);

public sealed record ListaDto(long Id, string Nombre, string? Descripcion, int Miembros, DateTime CreadaUtc, string CreadaPor);

public sealed record CrearListaRequest(string? Nombre, string? Descripcion);

public sealed record MiembrosRequest(IReadOnlyList<long>? Contactos);

public sealed record ImportacionDto(
    string Archivo, int Filas, int Validas, int Errores, int Creados, int Actualizados, int OptIn, int OptOut, int Advertencias,
    IReadOnlyList<ImportRowResult> Detalle);

/// <summary>
/// Contactos, mensajes recientes, oferta de promociones, importación y listas (F1.4). Todo con la política PuedeEnviar:
/// lo usa el encargado que atiende a los clientes (Operador). La lógica y la auditoría están en los servicios de
/// Infrastructure/Contacts; aquí solo se traduce HTTP ↔ servicio.
/// </summary>
public static class ContactsEndpoints
{
    /// <summary>Tamaño máximo del archivo de importación.</summary>
    public const long MaxImportBytes = 5 * 1024 * 1024;

    public static RouteGroupBuilder MapContactsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/mensajes", async (ConsentOfferService offers, CancellationToken ct) =>
                Results.Ok((await offers.RecentAsync(7, ct)).Select(c => new ConversacionDto(
                    c.ContactId, c.PhoneE164, c.Name, c.LastType, c.LastText, c.LastUtc, c.WindowOpenUntilUtc, c.Consent,
                    c.Refusal is null, c.RefusalMessage, c.LastOfferStatus, c.LastOfferResponse))))
            .RequireAuthorization(Policies.PuedeEnviar);

        var contacts = api.MapGroup("/contactos").RequireAuthorization(Policies.PuedeEnviar);

        contacts.MapGet("", async (string? consentimiento, string? buscar, long? lista, ContactService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(consentimiento, buscar, ct, lista)).Select(ToDto)));

        contacts.MapGet("/{id:long}", async (long id, ContactService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } d
                ? Results.Ok(new ContactoDetalleDto(ToDto(d.Contact), d.UserId, d.History, d.Messages, d.Offers, d.Segments))
                : Results.NotFound(new ErrorDto("NoExiste", "El contacto no existe.")));

        contacts.MapPost("/{id:long}/ofrecer", async (long id, ClaimsPrincipal me, ConsentOfferService offers, CancellationToken ct) =>
        {
            var r = await offers.OfferAsync(Actor(me), id, ct);
            return r.Refusal switch
            {
                OfferRefusals.NoExiste => Results.NotFound(new ErrorDto(r.Refusal, r.Message)),
                not null => Results.BadRequest(new ErrorDto(r.Refusal, r.Message)),
                // Enviada, rechazada por Meta o incierta: la oferta quedó registrada; el mensaje dice qué pasó.
                null => Results.Ok(new OfertaDto(r.Sent, r.Message, r.Offer?.Id, r.Offer?.Status.ToString())),
            };
        });

        contacts.MapPost("/{id:long}/baja", async (long id, BajaRequest request, ClaimsPrincipal me, ContactService service,
            CancellationToken ct) => ToResult(await service.RecordOptOutAsync(Actor(me), id, request.Motivo, ct)));

        // Multipart: el antiforgery automático de los formularios de Minimal API se apaga porque el XSRF ya lo valida
        // XsrfValidationFilter (encabezado X-XSRF-TOKEN), igual que en el resto de /api.
        contacts.MapPost("/importar", async (IFormFile? archivo, long? lista, ClaimsPrincipal me, ContactImportService importer,
                SegmentService segments, CancellationToken ct) =>
            {
                if (archivo is null || archivo.Length == 0)
                {
                    return Results.BadRequest(new ErrorDto("SinArchivo", "Elige un archivo .csv o .xlsx."));
                }

                if (archivo.Length > MaxImportBytes)
                {
                    return Results.BadRequest(new ErrorDto("ArchivoGrande", "El archivo supera los 5 MB."));
                }

                if (lista is { } sid && !await segments.ExistsAsync(sid, ct))
                {
                    return Results.BadRequest(new ErrorDto("ListaNoExiste", "La lista elegida no existe."));
                }

                var fileName = Path.GetFileName(archivo.FileName);
                try
                {
                    await using var stream = archivo.OpenReadStream();
                    var table = ContactImportService.ReadTable(stream, fileName);
                    var r = await importer.ImportAsync(Actor(me), fileName, table, lista, ct);
                    return Results.Ok(new ImportacionDto(r.FileName, r.Rows, r.Valid, r.Errors, r.Created, r.Updated, r.OptIns,
                        r.OptOuts, r.Warnings, r.Detail));
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new ErrorDto("ArchivoInvalido", ex.Message));
                }
            })
            .DisableAntiforgery();

        var lists = api.MapGroup("/listas").RequireAuthorization(Policies.PuedeEnviar);

        lists.MapGet("", async (SegmentService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToDto)));

        lists.MapPost("", async (CrearListaRequest request, ClaimsPrincipal me, SegmentService service, CancellationToken ct) =>
        {
            var (result, segment) = await service.CreateAsync(Actor(me), request.Nombre, request.Descripcion, ct);
            return result.Succeeded
                ? Results.Created($"/api/listas/{segment!.Id}", ToDto(segment))
                : Results.BadRequest(new ErrorDto(result.Error!, result.Message!));
        });

        lists.MapGet("/{id:long}/miembros", async (long id, SegmentService segments, ContactService service, CancellationToken ct) =>
            await segments.ExistsAsync(id, ct)
                ? Results.Ok((await service.ListAsync(null, null, ct, id)).Select(ToDto))
                : Results.NotFound(new ErrorDto("NoExiste", "La lista no existe.")));

        lists.MapPost("/{id:long}/miembros", async (long id, MiembrosRequest request, ClaimsPrincipal me, SegmentService service,
            CancellationToken ct) =>
        {
            var (result, added) = await service.AddMembersAsync(Actor(me), id, request.Contactos ?? [], ct);
            return result.Succeeded ? Results.Ok(new { agregados = added }) : ToResult(result);
        });

        lists.MapDelete("/{id:long}/miembros/{contactId:long}", async (long id, long contactId, ClaimsPrincipal me,
            SegmentService service, CancellationToken ct) => ToResult(await service.RemoveMemberAsync(Actor(me), id, contactId, ct)));

        return api;
    }

    private static string Actor(ClaimsPrincipal me) => me.Identity?.Name ?? "desconocido";

    private static ContactoDto ToDto(ContactSummary c) =>
        new(c.Id, c.PhoneE164, c.Name, c.CustomerId, c.IsActive, c.Consent, c.PlatformStop, c.LastInboundUtc);

    private static ListaDto ToDto(SegmentSummary s) => new(s.Id, s.Name, s.Description, s.Members, s.CreatedUtc, s.CreatedBy);

    private static IResult ToResult(ContactActionResult result) =>
        result.Succeeded ? Results.NoContent()
        : result.Error == "NoExiste" ? Results.NotFound(new ErrorDto(result.Error, result.Message!))
        : Results.BadRequest(new ErrorDto(result.Error!, result.Message!));
}
