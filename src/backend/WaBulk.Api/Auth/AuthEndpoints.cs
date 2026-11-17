using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Identity;

namespace WaBulk.Api.Auth;

public sealed record LoginRequest(string Usuario, string Clave);

public sealed record SessionResponse(bool Autenticado, string? Usuario, IReadOnlyList<string> Roles);

public sealed record LoginError(string Error, string Mensaje);

public sealed record UsuarioResumen(string Usuario, IReadOnlyList<string> Roles, DateTimeOffset? BloqueadoHasta);

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        // Estado de la sesión + cookie XSRF. Angular lo llama al arrancar, antes de cualquier POST (incluido el login).
        auth.MapGet("/sesion", (HttpContext http, IAntiforgery antiforgery) =>
        {
            XsrfValidationFilter.IssueToken(antiforgery, http);
            return Results.Ok(Session(http.User));
        }).AllowAnonymous();

        auth.MapPost("/login", async (LoginRequest request, HttpContext http, SignInManager<AppUser> signIn,
            UserManager<AppUser> users, IAntiforgery antiforgery) =>
        {
            if (string.IsNullOrWhiteSpace(request.Usuario) || string.IsNullOrEmpty(request.Clave))
            {
                return Unauthorized("Credenciales", "Usuario o clave incorrectos.");
            }

            var result = await signIn.PasswordSignInAsync(request.Usuario.Trim(), request.Clave,
                isPersistent: false, lockoutOnFailure: true);
            if (result.IsLockedOut)
            {
                return Unauthorized("Bloqueado",
                    $"Usuario bloqueado tras {AuthServiceCollectionExtensions.MaxFailedAccessAttempts} intentos fallidos. Intenta en 15 minutos o pide a un Admin que lo desbloquee.");
            }

            if (!result.Succeeded)
            {
                return Unauthorized("Credenciales", "Usuario o clave incorrectos.");
            }

            // El token XSRF va ligado al usuario: el emitido como anónimo ya no sirve. Se emite uno nuevo con el
            // usuario recién autenticado para que Angular no tenga que pedirlo aparte.
            var user = await users.FindByNameAsync(request.Usuario.Trim());
            http.User = await signIn.CreateUserPrincipalAsync(user!);
            XsrfValidationFilter.IssueToken(antiforgery, http);
            return Results.Ok(Session(http.User));
        }).AllowAnonymous();

        auth.MapPost("/logout", async (HttpContext http, SignInManager<AppUser> signIn, IAntiforgery antiforgery) =>
        {
            await signIn.SignOutAsync();
            http.User = new ClaimsPrincipal(new ClaimsIdentity());
            XsrfValidationFilter.IssueToken(antiforgery, http);
            return Results.Ok(Session(http.User));
        });

        // Lista mínima de usuarios (solo Admin); F1.3 la completa con crear, desactivar, cambiar rol y clave.
        api.MapGet("/usuarios", async (UserManager<AppUser> users) =>
        {
            var list = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
            var result = new List<UsuarioResumen>(list.Count);
            foreach (var u in list)
            {
                result.Add(new UsuarioResumen(u.UserName!, [.. await users.GetRolesAsync(u)], u.LockoutEnd));
            }

            return Results.Ok(result);
        }).RequireAuthorization(Policies.PuedeAdministrar);

        return api;
    }

    private static SessionResponse Session(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? new SessionResponse(true, user.Identity.Name,
                [.. user.FindAll(ClaimTypes.Role).Select(c => c.Value).Where(Roles.All.Contains)])
            : new SessionResponse(false, null, []);

    private static IResult Unauthorized(string error, string message) =>
        Results.Json(new LoginError(error, message), statusCode: StatusCodes.Status401Unauthorized);
}
