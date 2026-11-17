using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Identity;

namespace WaBulk.Api.Users;

public sealed record UsuarioDto(string Id, string Usuario, IReadOnlyList<string> Roles, bool Activo, DateTimeOffset? BloqueadoHasta);

public sealed record CrearUsuarioRequest(string Usuario, IReadOnlyList<string> Roles, string ClaveTemporal);

public sealed record RolesRequest(IReadOnlyList<string> Roles);

public sealed record ClaveRequest(string ClaveTemporal);

public sealed record ErrorDto(string Error, string Mensaje);

/// <summary>
/// Administración de usuarios (F1.3), solo con la política PuedeAdministrar. Toda la lógica y la auditoría están en
/// <see cref="UserAdminService"/>; aquí solo se traduce HTTP ↔ servicio.
/// </summary>
public static class UsersEndpoints
{
    public static RouteGroupBuilder MapUsersEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/usuarios").RequireAuthorization(Policies.PuedeAdministrar);

        users.MapGet("", async (UserAdminService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToDto)));

        users.MapGet("/roles", () => Results.Ok(Roles.All));

        users.MapPost("", async (CrearUsuarioRequest request, ClaimsPrincipal me, UserAdminService service,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Usuario))
            {
                return Results.BadRequest(new ErrorDto("SinUsuario", "Escribe el nombre de usuario."));
            }

            return ToResult(await service.CreateAsync(Actor(me), request.Usuario, request.Roles ?? [],
                request.ClaveTemporal ?? "", ct), created: true);
        });

        users.MapPut("/{id}/roles", async (string id, RolesRequest request, ClaimsPrincipal me, UserAdminService service,
            CancellationToken ct) => ToResult(await service.SetRolesAsync(Actor(me), id, request.Roles ?? [], ct)));

        users.MapPost("/{id}/desactivar", async (string id, ClaimsPrincipal me, UserManager<AppUser> userManager,
            UserAdminService service, CancellationToken ct) =>
            ToResult(await service.SetActiveAsync(Actor(me), userManager.GetUserId(me)!, id, active: false, ct)));

        users.MapPost("/{id}/activar", async (string id, ClaimsPrincipal me, UserManager<AppUser> userManager,
            UserAdminService service, CancellationToken ct) =>
            ToResult(await service.SetActiveAsync(Actor(me), userManager.GetUserId(me)!, id, active: true, ct)));

        users.MapPost("/{id}/clave", async (string id, ClaveRequest request, ClaimsPrincipal me, UserAdminService service,
            CancellationToken ct) => ToResult(await service.ResetPasswordAsync(Actor(me), id, request.ClaveTemporal ?? "", ct)));

        users.MapPost("/{id}/desbloquear", async (string id, ClaimsPrincipal me, UserAdminService service,
            CancellationToken ct) => ToResult(await service.UnlockAsync(Actor(me), id, ct)));

        return api;
    }

    /// <summary>Quién hace el cambio, para AuditLog: el nombre de usuario de la sesión.</summary>
    private static string Actor(ClaimsPrincipal me) => me.Identity?.Name ?? "desconocido";

    private static UsuarioDto ToDto(UserSummary u) => new(u.Id, u.UserName, u.Roles, u.IsActive, u.LockoutEnd);

    private static IResult ToResult(UserAdminResult result, bool created = false)
    {
        if (result.Succeeded)
        {
            var dto = ToDto(result.User!);
            return created ? Results.Created($"/api/usuarios/{dto.Id}", dto) : Results.Ok(dto);
        }

        var error = new ErrorDto(result.Error!, result.Message!);
        return result.Error == "NoExiste" ? Results.NotFound(error) : Results.BadRequest(error);
    }
}
