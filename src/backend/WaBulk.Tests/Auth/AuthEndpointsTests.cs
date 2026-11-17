using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WaBulk.Api;
using WaBulk.Api.Auth;
using WaBulk.Api.Users;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Identity;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Auth;

/// <summary>
/// F1.2: login con cookie, roles, bloqueo y XSRF contra la Api real (WebApplicationFactory) y SQL Server real.
/// Cada prueba usa usuarios propios, así comparten la base de la clase sin pisarse.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthEndpointsTests(SqlServerTestDatabase db) : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private const string GoodPassword = "Clave-Segura-2026";
    private WebApplicationFactory<ApiAssemblyMarker> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:WaBulk", db.ConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task SinSesion_EndpointProtegido_Responde401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/usuarios", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SinSesion_Sesion_DiceNoAutenticadoYEmiteCookieXsrf()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/auth/sesion", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.False(session!.Autenticado);
        Assert.NotNull(ApiSession.ReadXsrfCookie(response));
    }

    [Fact]
    public async Task RolIncorrecto_Responde403()
    {
        await CreateUserAsync("operador1", Roles.Operador);
        var api = new ApiSession(_factory.CreateClient());
        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync("operador1", GoodPassword)).StatusCode);

        var response = await api.Client.GetAsync(new Uri("/api/usuarios", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_VeUsuariosYSusRoles()
    {
        await CreateUserAsync("admin1", Roles.Admin);
        var api = new ApiSession(_factory.CreateClient());

        var login = await api.LoginAsync("admin1", GoodPassword);
        var session = await login.Content.ReadFromJsonAsync<SessionResponse>();
        var response = await api.Client.GetAsync(new Uri("/api/usuarios", UriKind.Relative));

        Assert.Equal(["Admin"], session!.Roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<UsuarioDto>>();
        Assert.Contains(users!, u => u.Usuario == "admin1" && u.Roles.SequenceEqual(["Admin"]));
    }

    [Fact]
    public async Task CincoClavesMalas_BloqueaAunqueLaSextaSeaCorrecta()
    {
        await CreateUserAsync("bloqueo1", Roles.Operador);
        var api = new ApiSession(_factory.CreateClient());

        for (var i = 0; i < AuthServiceCollectionExtensions.MaxFailedAccessAttempts; i++)
        {
            var bad = await api.LoginAsync("bloqueo1", "clave-mala");
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        var good = await api.LoginAsync("bloqueo1", GoodPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, good.StatusCode);
        Assert.Equal("Bloqueado", (await good.Content.ReadFromJsonAsync<LoginError>())!.Error);
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByNameAsync("bloqueo1"))!));
    }

    [Fact]
    public async Task CuatroClavesMalas_NoBloquea()
    {
        await CreateUserAsync("bloqueo2", Roles.Operador);
        var api = new ApiSession(_factory.CreateClient());

        for (var i = 0; i < AuthServiceCollectionExtensions.MaxFailedAccessAttempts - 1; i++)
        {
            await api.LoginAsync("bloqueo2", "clave-mala");
        }

        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync("bloqueo2", GoodPassword)).StatusCode);
    }

    [Fact]
    public async Task PostSinTokenXsrf_Rechazado()
    {
        await CreateUserAsync("xsrf1", Roles.Operador);
        var client = _factory.CreateClient();
        await client.GetAsync(new Uri("/api/auth/sesion", UriKind.Relative)); // hay cookie, pero no encabezado

        var response = await client.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest("xsrf1", GoodPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(ApiSession.ReadAuthCookie(response)); // no inició sesión
    }

    [Fact]
    public async Task PostConTokenFalso_Rechazado()
    {
        var client = _factory.CreateClient();
        await client.GetAsync(new Uri("/api/auth/sesion", UriKind.Relative));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/login", UriKind.Relative))
        {
            Content = JsonContent.Create(new LoginRequest("x", "y")),
        };
        request.Headers.Add(AuthServiceCollectionExtensions.XsrfHeaderName, "token-inventado");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConSesion_PostSinTokenXsrf_Rechazado_YConTokenRenovado_Acepta()
    {
        await CreateUserAsync("xsrf2", Roles.Aprobador);
        var api = new ApiSession(_factory.CreateClient());
        await api.LoginAsync("xsrf2", GoodPassword);

        var withoutToken = await api.Client.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), null);
        var withToken = await api.PostAsync("/api/auth/logout"); // usa el token emitido al iniciar sesión

        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await api.Client.GetAsync(new Uri("/api/usuarios", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task CrearPrimerAdmin_SoloUnaVez()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<AdminBootstrapper>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        foreach (var admin in await users.GetUsersInRoleAsync(Roles.Admin))
        {
            await users.DeleteAsync(admin); // la prueba parte de una base sin Admin
        }

        var weak = await bootstrapper.CreateFirstAdminAsync("primero", "corta", "windows:prueba");
        var first = await bootstrapper.CreateFirstAdminAsync("primero", GoodPassword, "windows:prueba");
        var second = await bootstrapper.CreateFirstAdminAsync("segundo", GoodPassword, "windows:prueba");

        Assert.False(weak.Succeeded);
        Assert.True(first.Succeeded);
        Assert.True(await users.IsInRoleAsync((await users.FindByNameAsync("primero"))!, Roles.Admin));
        Assert.Equal("AdminExiste", Assert.Single(second.Errors).Code);
        Assert.Null(await users.FindByNameAsync("segundo"));
    }

    private async Task CreateUserAsync(string userName, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName };
        Assert.True((await users.CreateAsync(user, GoodPassword)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
    }
}

/// <summary>Cliente que se comporta como Angular: guarda la cookie XSRF-TOKEN y la copia al encabezado en los POST.</summary>
internal sealed class ApiSession(HttpClient client)
{
    private string? _xsrf;

    public HttpClient Client { get; } = client;

    public async Task<HttpResponseMessage> LoginAsync(string userName, string password)
    {
        if (_xsrf is null)
        {
            _xsrf = ReadXsrfCookie(await Client.GetAsync(new Uri("/api/auth/sesion", UriKind.Relative)));
        }

        return await PostAsync("/api/auth/login", new LoginRequest(userName, password));
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body);

    public Task<HttpResponseMessage> PutAsync(string path, object? body) => SendAsync(HttpMethod.Put, path, body);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add(AuthServiceCollectionExtensions.XsrfHeaderName, _xsrf);
        var response = await Client.SendAsync(request);
        _xsrf = ReadXsrfCookie(response) ?? _xsrf;
        return response;
    }

    public static string? ReadXsrfCookie(HttpResponseMessage response) =>
        ReadCookie(response, AuthServiceCollectionExtensions.XsrfCookieName);

    public static string? ReadAuthCookie(HttpResponseMessage response) =>
        ReadCookie(response, AuthServiceCollectionExtensions.AuthCookieName);

    private static string? ReadCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        var prefix = name + "=";
        var cookie = cookies.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return cookie is null ? null : Uri.UnescapeDataString(cookie[prefix.Length..].Split(';')[0]);
    }
}
