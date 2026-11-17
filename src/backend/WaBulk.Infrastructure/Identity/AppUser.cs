using Microsoft.AspNetCore.Identity;

namespace WaBulk.Infrastructure.Identity;

/// <summary>
/// Usuario de la intranet (ASP.NET Core Identity, tablas AspNet* en la misma base). Tipo propio desde ya para que
/// F1.3 sume campos (Activo…) sin cambiar el tipo en todo el código.
/// </summary>
public class AppUser : IdentityUser
{
}
