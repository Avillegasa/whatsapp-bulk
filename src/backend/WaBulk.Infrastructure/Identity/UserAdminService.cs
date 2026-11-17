using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Identity;

public sealed record UserSummary(string Id, string UserName, IReadOnlyList<string> Roles, bool IsActive, DateTimeOffset? LockoutEnd);

/// <summary>Resultado de una operación de administración. <see cref="Error"/> es un código estable para la interfaz.</summary>
public sealed record UserAdminResult(bool Succeeded, string? Error = null, string? Message = null, UserSummary? User = null)
{
    public static UserAdminResult Ok(UserSummary user) => new(true, User: user);
    public static UserAdminResult Fail(string error, string message) => new(false, error, message);
    public static readonly UserAdminResult NotFound = Fail("NoExiste", "El usuario no existe.");
}

/// <summary>
/// Administración de usuarios (F1.3, pantalla de Admin). Cada operación corre en UNA transacción que incluye el
/// cambio (UserManager guarda en el mismo WaBulkDbContext) y su fila en AuditLog: o quedan los dos o ninguno.
/// Reglas: al menos un rol válido; nunca queda el sistema sin un Admin activo; nadie se desactiva a sí mismo.
/// Las claves nunca van a la auditoría.
/// </summary>
public sealed class UserAdminService(UserManager<AppUser> users, WaBulkDbContext db, TimeProvider clock)
{
    private const string Entity = "Usuario";

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var list = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken);
        var result = new List<UserSummary>(list.Count);
        foreach (var u in list)
        {
            result.Add(await SummaryAsync(u));
        }

        return result;
    }

    public async Task<UserAdminResult> CreateAsync(string actor, string userName, IReadOnlyCollection<string> roles,
        string temporaryPassword, CancellationToken cancellationToken)
    {
        userName = userName.Trim();
        if (InvalidRoles(roles) is { } invalid)
        {
            return invalid;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = new AppUser { UserName = userName };
        var created = await users.CreateAsync(user, temporaryPassword);
        if (!created.Succeeded)
        {
            return FromIdentity(created);
        }

        var added = await users.AddToRolesAsync(user, roles.Distinct());
        if (!added.Succeeded)
        {
            return FromIdentity(added);
        }

        var after = await SummaryAsync(user);
        return await CommitAsync(tx, actor, AuditActions.CrearUsuario, user.Id, null, Snapshot(after), after, cancellationToken);
    }

    public async Task<UserAdminResult> SetRolesAsync(string actor, string userId, IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        if (InvalidRoles(roles) is { } invalid)
        {
            return invalid;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return UserAdminResult.NotFound;
        }

        var before = await SummaryAsync(user);
        var wanted = roles.Distinct().ToList();
        if (before.Roles.Contains(Roles.Admin) && !wanted.Contains(Roles.Admin) && user.IsActive
            && await OtherActiveAdminsAsync(user.Id, cancellationToken) == 0)
        {
            return UserAdminResult.Fail("UltimoAdmin", "No se puede quitar el rol Admin al último Admin activo.");
        }

        var removed = await users.RemoveFromRolesAsync(user, before.Roles.Except(wanted));
        if (!removed.Succeeded)
        {
            return FromIdentity(removed);
        }

        var added = await users.AddToRolesAsync(user, wanted.Except(before.Roles));
        if (!added.Succeeded)
        {
            return FromIdentity(added);
        }

        // Sin cambiar el sello: con la revalidación en cada petición, la sesión abierta toma los roles nuevos sola.
        var after = await SummaryAsync(user);
        return await CommitAsync(tx, actor, AuditActions.CambiarRoles, user.Id, Snapshot(before), Snapshot(after), after,
            cancellationToken);
    }

    public async Task<UserAdminResult> SetActiveAsync(string actor, string actorId, string userId, bool active,
        CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return UserAdminResult.NotFound;
        }

        if (!active && user.Id == actorId)
        {
            return UserAdminResult.Fail("NoASiMismo", "No puedes desactivar tu propio usuario.");
        }

        var before = await SummaryAsync(user);
        if (before.IsActive == active)
        {
            return UserAdminResult.Ok(before); // nada que cambiar, nada que auditar
        }

        if (!active && before.Roles.Contains(Roles.Admin) && await OtherActiveAdminsAsync(user.Id, cancellationToken) == 0)
        {
            return UserAdminResult.Fail("UltimoAdmin", "No se puede desactivar al último Admin activo.");
        }

        user.IsActive = active;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return FromIdentity(updated);
        }

        if (!active)
        {
            // Además de Activo = 0: cambia el sello, así cualquier cookie emitida antes deja de valer.
            await users.UpdateSecurityStampAsync(user);
        }

        var after = await SummaryAsync(user);
        return await CommitAsync(tx, actor, active ? AuditActions.ActivarUsuario : AuditActions.DesactivarUsuario, user.Id,
            Snapshot(before), Snapshot(after), after, cancellationToken);
    }

    public async Task<UserAdminResult> ResetPasswordAsync(string actor, string userId, string temporaryPassword,
        CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return UserAdminResult.NotFound;
        }

        // ResetPasswordAsync valida la clave contra las reglas y cambia el sello: se cierran las sesiones abiertas.
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, temporaryPassword);
        if (!reset.Succeeded)
        {
            return FromIdentity(reset);
        }

        var after = await SummaryAsync(user);
        return await CommitAsync(tx, actor, AuditActions.RestablecerClave, user.Id, null,
            new { usuario = user.UserName, clave = "restablecida por un Admin" }, after, cancellationToken);
    }

    public async Task<UserAdminResult> UnlockAsync(string actor, string userId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return UserAdminResult.NotFound;
        }

        var before = new { usuario = user.UserName, bloqueadoHasta = user.LockoutEnd, intentosFallidos = user.AccessFailedCount };
        var unlocked = await users.SetLockoutEndDateAsync(user, null);
        if (!unlocked.Succeeded)
        {
            return FromIdentity(unlocked);
        }

        await users.ResetAccessFailedCountAsync(user);
        var after = await SummaryAsync(user);
        return await CommitAsync(tx, actor, AuditActions.DesbloquearUsuario, user.Id, before,
            new { usuario = user.UserName, bloqueadoHasta = (DateTimeOffset?)null, intentosFallidos = 0 }, after,
            cancellationToken);
    }

    private async Task<UserAdminResult> CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        string actor, string action, string userId, object? before, object? after, UserSummary result,
        CancellationToken cancellationToken)
    {
        db.Set<AuditLog>().Add(AuditEntry.Create(actor, action, Entity, userId, before, after, clock.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return UserAdminResult.Ok(result);
    }

    private async Task<int> OtherActiveAdminsAsync(string exceptUserId, CancellationToken cancellationToken)
    {
        var adminRoleId = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync(cancellationToken);
        return await db.UserRoles
            .Where(ur => ur.RoleId == adminRoleId && ur.UserId != exceptUserId)
            .Join(db.Users.Where(u => u.IsActive), ur => ur.UserId, u => u.Id, (ur, u) => u.Id)
            .CountAsync(cancellationToken);
    }

    private async Task<UserSummary> SummaryAsync(AppUser u) =>
        new(u.Id, u.UserName!, [.. (await users.GetRolesAsync(u)).Order()], u.IsActive, u.LockoutEnd);

    private static object Snapshot(UserSummary s) => new { usuario = s.UserName, roles = s.Roles, activo = s.IsActive };

    private static UserAdminResult? InvalidRoles(IReadOnlyCollection<string> roles)
    {
        if (roles.Count == 0)
        {
            return UserAdminResult.Fail("SinRol", "El usuario necesita al menos un rol.");
        }

        var unknown = roles.Where(r => !Roles.All.Contains(r)).ToList();
        return unknown.Count == 0
            ? null
            : UserAdminResult.Fail("RolInvalido", $"Rol desconocido: {string.Join(", ", unknown)}.");
    }

    private static UserAdminResult FromIdentity(IdentityResult result)
    {
        var first = result.Errors.FirstOrDefault();
        return UserAdminResult.Fail(first?.Code ?? "Error",
            string.Join(" ", result.Errors.Select(e => IdentityMessages.Translate(e))));
    }
}

/// <summary>Mensajes de Identity en español para la pantalla (los códigos son los de IdentityErrorDescriber).</summary>
public static class IdentityMessages
{
    public static string Translate(IdentityError e) => e.Code switch
    {
        "DuplicateUserName" => "Ya existe un usuario con ese nombre.",
        "InvalidUserName" => "El nombre de usuario solo admite letras, números y - . _ @ +.",
        "PasswordTooShort" => "La clave debe tener al menos 10 caracteres.",
        "PasswordRequiresDigit" => "La clave necesita al menos un número.",
        "PasswordRequiresLower" => "La clave necesita al menos una minúscula.",
        "PasswordRequiresUpper" => "La clave necesita al menos una mayúscula.",
        "PasswordRequiresNonAlphanumeric" => "La clave necesita al menos un símbolo (- ! # . …).",
        "PasswordRequiresUniqueChars" => "La clave necesita más caracteres distintos.",
        _ => e.Description,
    };
}
