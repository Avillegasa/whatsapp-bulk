using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaBulk.Api;
using WaBulk.Api.Auth;
using WaBulk.Api.Users;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Identity;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Auth;

/// <summary>
/// F1.3: administración de usuarios por HTTP como lo hace la pantalla (Admin con cookie + XSRF), auditoría en la
/// misma transacción, usuario desactivado fuera de inmediato. SQL Server real (transacciones de verdad).
/// </summary>
[Trait("Category", "Integration")]
public sealed class UserAdminTests(SqlServerTestDatabase db) : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private const string Password = "Clave-Segura-2026";
    private const string NewPassword = "Otra-Clave-2027!";
    private WebApplicationFactory<ApiAssemblyMarker> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:WaBulk", db.ConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task CambioDeRoles_QuedaEnAuditLogConAntesYDespues()
    {
        var admin = await AdminSessionAsync("admin-roles");
        var target = await CreateUserAsync("ana", Roles.Operador);

        var response = await admin.PutAsync($"/api/usuarios/{target}/roles", new RolesRequest([Roles.Operador, Roles.Aprobador]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Roles.Aprobador, Roles.Operador], (await response.Content.ReadFromJsonAsync<UsuarioDto>())!.Roles);
        var audit = Assert.Single(await AuditAsync(target, AuditActions.CambiarRoles));
        Assert.Equal("admin-roles", audit.User);
        Assert.Equal("""{"usuario":"ana","roles":["Operador"],"activo":true}""", audit.Before);
        Assert.Equal("""{"usuario":"ana","roles":["Aprobador","Operador"],"activo":true}""", audit.After);
    }

    [Fact]
    public async Task CambioDeRoles_LaSesionAbiertaLosTomaSinVolverAEntrar()
    {
        var admin = await AdminSessionAsync("admin-promueve");
        var target = await CreateUserAsync("beto", Roles.Operador);
        var beto = await SessionAsync("beto", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await beto.Client.GetAsync(Rel("/api/usuarios"))).StatusCode);

        await admin.PutAsync($"/api/usuarios/{target}/roles", new RolesRequest([Roles.Admin]));

        Assert.Equal(HttpStatusCode.OK, (await beto.Client.GetAsync(Rel("/api/usuarios"))).StatusCode);
    }

    [Fact]
    public async Task UsuarioDesactivado_NoIniciaSesion_YPierdeLaSesionAbierta()
    {
        var admin = await AdminSessionAsync("admin-desactiva");
        var target = await CreateUserAsync("carla", Roles.Operador);
        var abierta = await SessionAsync("carla", Password);
        Assert.Equal(HttpStatusCode.OK, (await abierta.Client.GetAsync(Rel("/api/auth/sesion"))).StatusCode);

        var response = await admin.PostAsync($"/api/usuarios/{target}/desactivar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<UsuarioDto>())!.Activo);
        // Sesión abierta: la siguiente petición ya no la reconoce.
        var sesion = await (await abierta.Client.GetAsync(Rel("/api/auth/sesion"))).Content.ReadFromJsonAsync<SessionResponse>();
        Assert.False(sesion!.Autenticado);
        Assert.Equal(HttpStatusCode.Unauthorized, (await abierta.PostAsync("/api/auth/logout")).StatusCode);
        // Login nuevo: rechazado con el mismo mensaje que una clave mala.
        var login = await new ApiSession(_factory.CreateClient()).LoginAsync("carla", Password);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("Credenciales", (await login.Content.ReadFromJsonAsync<LoginError>())!.Error);
        var audit = Assert.Single(await AuditAsync(target, AuditActions.DesactivarUsuario));
        Assert.Contains("\"activo\":true", audit.Before, StringComparison.Ordinal);
        Assert.Contains("\"activo\":false", audit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsuarioReactivado_VuelveAEntrar()
    {
        var admin = await AdminSessionAsync("admin-reactiva");
        var target = await CreateUserAsync("dani", Roles.Aprobador);
        await admin.PostAsync($"/api/usuarios/{target}/desactivar");

        var response = await admin.PostAsync($"/api/usuarios/{target}/activar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await new ApiSession(_factory.CreateClient()).LoginAsync("dani", Password)).StatusCode);
        Assert.Single(await AuditAsync(target, AuditActions.ActivarUsuario));
    }

    [Fact]
    public async Task RestablecerClave_LaViejaNoSirve_CierraSesiones_YNoGuardaLaClave()
    {
        var admin = await AdminSessionAsync("admin-clave");
        var target = await CreateUserAsync("eva", Roles.Operador);
        var abierta = await SessionAsync("eva", Password);

        var response = await admin.PostAsync($"/api/usuarios/{target}/clave", new ClaveRequest(NewPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new ApiSession(_factory.CreateClient()).LoginAsync("eva", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await new ApiSession(_factory.CreateClient()).LoginAsync("eva", NewPassword)).StatusCode);
        Assert.False((await (await abierta.Client.GetAsync(Rel("/api/auth/sesion"))).Content.ReadFromJsonAsync<SessionResponse>())!.Autenticado);
        var audit = Assert.Single(await AuditAsync(target, AuditActions.RestablecerClave));
        Assert.DoesNotContain(NewPassword, audit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestablecerClave_Debil_Rechazada_SinAuditoria()
    {
        var admin = await AdminSessionAsync("admin-clave-debil");
        var target = await CreateUserAsync("fede", Roles.Operador);

        var response = await admin.PostAsync($"/api/usuarios/{target}/clave", new ClaveRequest("corta"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("10 caracteres", (await response.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje, StringComparison.Ordinal);
        Assert.Empty(await AuditAsync(target, AuditActions.RestablecerClave));
    }

    [Fact]
    public async Task Desbloquear_PermiteEntrarDeNuevo()
    {
        var admin = await AdminSessionAsync("admin-desbloquea");
        var target = await CreateUserAsync("gabi", Roles.Operador);
        var intento = new ApiSession(_factory.CreateClient());
        for (var i = 0; i < AuthServiceCollectionExtensions.MaxFailedAccessAttempts; i++)
        {
            await intento.LoginAsync("gabi", "clave-mala");
        }

        Assert.Equal("Bloqueado", (await (await intento.LoginAsync("gabi", Password)).Content.ReadFromJsonAsync<LoginError>())!.Error);

        var response = await admin.PostAsync($"/api/usuarios/{target}/desbloquear");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await intento.LoginAsync("gabi", Password)).StatusCode);
        var audit = Assert.Single(await AuditAsync(target, AuditActions.DesbloquearUsuario));
        Assert.Contains("\"intentosFallidos\":0", audit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrearUsuario_ConVariosRoles_Auditado_YPuedeEntrar()
    {
        var admin = await AdminSessionAsync("admin-crea");

        var response = await admin.PostAsync("/api/usuarios",
            new CrearUsuarioRequest("hugo", [Roles.Operador, Roles.Aprobador], Password));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioDto>())!;
        Assert.Equal([Roles.Aprobador, Roles.Operador], dto.Roles);
        Assert.True(dto.Activo);
        var audit = Assert.Single(await AuditAsync(dto.Id, AuditActions.CrearUsuario));
        Assert.Null(audit.Before);
        Assert.DoesNotContain(Password, audit.After, StringComparison.Ordinal);
        var login = await new ApiSession(_factory.CreateClient()).LoginAsync("hugo", Password);
        Assert.Equal([Roles.Aprobador, Roles.Operador], (await login.Content.ReadFromJsonAsync<SessionResponse>())!.Roles.Order());
    }

    [Theory]
    [InlineData("", "SinRol")]
    [InlineData("Supervisor", "RolInvalido")]
    public async Task CrearUsuario_RolesInvalidos_Rechazado(string role, string error)
    {
        var admin = await AdminSessionAsync($"admin-rol-{error}");
        string[] roles = role.Length == 0 ? [] : [role];

        var response = await admin.PostAsync("/api/usuarios", new CrearUsuarioRequest($"x-{error}", roles, Password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await response.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync($"x-{error}"));
    }

    [Fact]
    public async Task CrearUsuario_Repetido_Rechazado()
    {
        var admin = await AdminSessionAsync("admin-repetido");
        await CreateUserAsync("ines", Roles.Operador);

        var response = await admin.PostAsync("/api/usuarios", new CrearUsuarioRequest("ines", [Roles.Operador], Password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Ya existe un usuario con ese nombre.", (await response.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task NadieSeDesactivaASiMismo()
    {
        var admin = await AdminSessionAsync("admin-yo");
        var me = await UserIdAsync("admin-yo");

        var response = await admin.PostAsync($"/api/usuarios/{me}/desactivar");

        Assert.Equal("NoASiMismo", (await response.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
    }

    [Fact]
    public async Task UsuarioQueNoExiste_404()
    {
        var admin = await AdminSessionAsync("admin-404");

        var response = await admin.PostAsync("/api/usuarios/no-existe/desactivar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SinXsrf_LaAdministracionSeRechaza()
    {
        await AdminSessionAsync("admin-xsrf");
        var sinToken = new ApiSession(_factory.CreateClient());
        await sinToken.LoginAsync("admin-xsrf", Password);
        var target = await CreateUserAsync("juan", Roles.Operador);

        var response = await sinToken.Client.PostAsync(Rel($"/api/usuarios/{target}/desactivar"), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await AuditAsync(target, AuditActions.DesactivarUsuario));
    }

    [Fact]
    public async Task OperadorYAprobador_NoAdministranUsuarios()
    {
        await CreateUserAsync("kike", Roles.Operador, Roles.Aprobador);
        var target = await CreateUserAsync("luis", Roles.Operador);
        var kike = await SessionAsync("kike", Password);

        Assert.Equal(HttpStatusCode.Forbidden, (await kike.Client.GetAsync(Rel("/api/usuarios"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kike.PostAsync($"/api/usuarios/{target}/desactivar")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await kike.PutAsync($"/api/usuarios/{target}/roles", new RolesRequest([Roles.Admin]))).StatusCode);
    }

    [Fact]
    public async Task SiFallaLaAuditoria_ElCambioSeRevierte()
    {
        // Misma transacción: si la fila de AuditLog no se puede guardar (aquí, Usuario de más de 256 caracteres),
        // el cambio de roles tampoco queda.
        var target = await CreateUserAsync("mara", Roles.Operador);
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<UserAdminService>();

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            service.SetRolesAsync(new string('x', 300), target, [Roles.Admin], CancellationToken.None));

        await using var check = db.CreateContext();
        var roles = await check.UserRoles.Where(ur => ur.UserId == target)
            .Join(check.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).ToListAsync();
        Assert.Equal([Roles.Operador], roles);
        Assert.Empty(await AuditAsync(target, AuditActions.CambiarRoles));
    }

    // ---------- Apoyo ----------

    private static Uri Rel(string path) => new(path, UriKind.Relative);

    private async Task<ApiSession> AdminSessionAsync(string userName)
    {
        await CreateUserAsync(userName, Roles.Admin);
        return await SessionAsync(userName, Password);
    }

    private async Task<ApiSession> SessionAsync(string userName, string password)
    {
        var session = new ApiSession(_factory.CreateClient());
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(userName, password)).StatusCode);
        return session;
    }

    private async Task<string> CreateUserAsync(string userName, params string[] roles)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRolesAsync(user, roles)).Succeeded);
        return user.Id;
    }

    private async Task<string> UserIdAsync(string userName)
    {
        await using var check = db.CreateContext();
        return await check.Users.Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync();
    }

    private async Task<List<AuditLog>> AuditAsync(string userId, string action)
    {
        await using var check = db.CreateContext();
        return await check.AuditLogs.AsNoTracking().Where(a => a.EntityId == userId && a.Action == action).ToListAsync();
    }
}

/// <summary>Reglas que dependen de cuántos Admin hay: base propia, sin los Admin de las otras pruebas.</summary>
[Trait("Category", "Integration")]
public sealed class LastAdminTests(SqlServerTestDatabase db) : IClassFixture<SqlServerTestDatabase>
{
    [Fact]
    public async Task NuncaQuedaElSistemaSinAdminActivo()
    {
        await using var factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:WaBulk", db.ConnectionString));
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var service = scope.ServiceProvider.GetRequiredService<UserAdminService>();
        var unico = new AppUser { UserName = "unico" };
        await users.CreateAsync(unico, "Clave-Segura-2026");
        await users.AddToRoleAsync(unico, Roles.Admin);
        var otro = new AppUser { UserName = "otro" };
        await users.CreateAsync(otro, "Clave-Segura-2026");
        await users.AddToRoleAsync(otro, Roles.Operador);

        var quitarAdmin = await service.SetRolesAsync("otro", unico.Id, [Roles.Operador], CancellationToken.None);
        var desactivar = await service.SetActiveAsync("otro", otro.Id, unico.Id, active: false, CancellationToken.None);

        Assert.Equal("UltimoAdmin", quitarAdmin.Error);
        Assert.Equal("UltimoAdmin", desactivar.Error);

        // Con un segundo Admin activo, sí se puede.
        await users.AddToRoleAsync(otro, Roles.Admin);
        Assert.True((await service.SetRolesAsync("otro", unico.Id, [Roles.Operador], CancellationToken.None)).Succeeded);
    }
}
