using Microsoft.AspNetCore.Identity;

namespace WaBulk.Infrastructure.Identity;

/// <summary>Usuario de la intranet (ASP.NET Core Identity, tablas AspNet* en la misma base).</summary>
public class AppUser : IdentityUser
{
    /// <summary>
    /// Columna Activo. Un usuario desactivado no inicia sesión y pierde la que tenía abierta
    /// (AppSignInManager, en la Api). No se borran usuarios: la auditoría los sigue nombrando.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
