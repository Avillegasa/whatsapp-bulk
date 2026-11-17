using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WaBulk.Infrastructure.Identity;

namespace WaBulk.Api.Auth;

/// <summary>
/// SignInManager que respeta <see cref="AppUser.IsActive"/>:
/// - al iniciar sesión: desactivado → NotAllowed (la Api responde como credenciales incorrectas);
/// - en cada petición: el validador del sello de seguridad (intervalo 0, ver AddWaBulkAuth) llama a
///   <see cref="ValidateSecurityStampAsync(ClaimsPrincipal?)"/>; si el usuario fue desactivado, la cookie se rechaza
///   y la sesión abierta termina en la siguiente petición.
/// </summary>
public sealed class AppSignInManager(
    UserManager<AppUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<AppUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<AppUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<AppUser> confirmation)
    : SignInManager<AppUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(AppUser user) =>
        user.IsActive && await base.CanSignInAsync(user);

    public override async Task<AppUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
    {
        var user = await base.ValidateSecurityStampAsync(principal);
        return user is { IsActive: true } ? user : null;
    }
}
