using Microsoft.AspNetCore.Identity;
using WaBulk.Domain.Users;

namespace WaBulk.Infrastructure.Identity;

/// <summary>
/// Crea el primer Admin (comando <c>crear-admin</c> de la Api). Solo funciona si todavía no hay ningún Admin:
/// después, los usuarios se administran desde la intranet (F1.3).
/// </summary>
public sealed class AdminBootstrapper(UserManager<AppUser> users)
{
    public async Task<IdentityResult> CreateFirstAdminAsync(string userName, string password)
    {
        if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "AdminExiste",
                Description = "Ya existe un Admin; los demás usuarios se crean desde la intranet.",
            });
        }

        var user = new AppUser { UserName = userName };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return created;
        }

        return await users.AddToRoleAsync(user, Roles.Admin);
    }
}
