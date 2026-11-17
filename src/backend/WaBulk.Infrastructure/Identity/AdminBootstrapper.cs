using Microsoft.AspNetCore.Identity;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Identity;

/// <summary>
/// Crea el primer Admin (comando <c>crear-admin</c> de la Api). Solo funciona si todavía no hay ningún Admin:
/// después, los usuarios se administran desde la intranet (F1.3). Queda auditado a nombre del usuario de Windows.
/// </summary>
public sealed class AdminBootstrapper(UserManager<AppUser> users, WaBulkDbContext db, TimeProvider clock)
{
    public async Task<IdentityResult> CreateFirstAdminAsync(string userName, string password, string actor)
    {
        if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "AdminExiste",
                Description = "Ya existe un Admin; los demás usuarios se crean desde la intranet.",
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        var user = new AppUser { UserName = userName };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return created;
        }

        var added = await users.AddToRoleAsync(user, Roles.Admin);
        if (!added.Succeeded)
        {
            return added;
        }

        db.Set<AuditLog>().Add(AuditEntry.Create(actor, AuditActions.CrearUsuario, "Usuario", user.Id, null,
            new { usuario = userName, roles = new[] { Roles.Admin }, activo = true, via = "crear-admin" },
            clock.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return IdentityResult.Success;
    }
}
