using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaBulk.Api;
using WaBulk.Api.Auth;
using WaBulk.Api.Contacts;
using WaBulk.Api.Users;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Identity;
using WaBulk.Tests.Auth;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Contacts;

/// <summary>F1.4 por HTTP, como lo usa la intranet: permisos, importación multipart con XSRF, baja y oferta sin ventana.</summary>
[Trait("Category", "Integration")]
public sealed class ContactsApiTests(SqlServerTestDatabase db) : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private const string Password = "Clave-Segura-2026";
    private WebApplicationFactory<ApiAssemblyMarker> _factory = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:WaBulk", db.ConnectionString));
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task SoloOperador_AdminSinOperadorRecibe403()
    {
        var admin = await SessionAsync("solo-admin", Roles.Admin);
        var operador = await SessionAsync("operador-1", Roles.Operador);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.GetAsync(Rel("/api/contactos"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.GetAsync(Rel("/api/mensajes"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operador.Client.GetAsync(Rel("/api/contactos"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operador.Client.GetAsync(Rel("/api/mensajes"))).StatusCode);
    }

    [Fact]
    public async Task Importar_CsvMultipartConXsrf_DevuelveElReportePorFila_YLoAudita()
    {
        var operador = await SessionAsync("operador-2", Roles.Operador);
        var csv = "Telefono;Nombre;Consentimiento;Evidencia\n70000001;Ana;OptIn;Formulario 12\n123;Malo;;\n";

        var response = await ImportAsync(operador, csv, withXsrf: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<ImportacionDto>())!;
        Assert.Equal((2, 1, 1, 1, 1), (report.Filas, report.Validas, report.Errores, report.Creados, report.OptIn));
        Assert.Equal(["Creado", "Error"], report.Detalle.Select(d => d.Result));
        await using var check = db.CreateContext();
        Assert.Equal("operador-2", (await check.AuditLogs.SingleAsync(a => a.Action == AuditActions.ImportarContactos)).User);

        // Sin el encabezado XSRF se rechaza igual que cualquier POST.
        Assert.Equal(HttpStatusCode.BadRequest, (await ImportAsync(operador, csv, withXsrf: false)).StatusCode);
    }

    [Fact]
    public async Task Contactos_FiltroPorEstado_BajaManual_YOfertaSinVentana()
    {
        var operador = await SessionAsync("operador-3", Roles.Operador);
        await ImportAsync(operador, "Telefono;Consentimiento;Evidencia\n70000002;OptIn;Formulario\n70000003;;\n", withXsrf: true);
        var optIn = (await operador.Client.GetFromJsonAsync<List<ContactoDto>>(Rel("/api/contactos?consentimiento=OptIn")))!;
        var sin = (await operador.Client.GetFromJsonAsync<List<ContactoDto>>(Rel("/api/contactos?consentimiento=SinConsentimiento")))!;
        Assert.Equal("+59170000002", Assert.Single(optIn).Telefono);
        var target = Assert.Single(sin);

        // Nunca escribió: no hay ventana, la oferta se rechaza sin llamar a Meta.
        var offer = await operador.PostAsync($"/api/contactos/{target.Id}/ofrecer");
        Assert.Equal(HttpStatusCode.BadRequest, offer.StatusCode);
        Assert.Equal(OfferRefusals.SinVentana, (await offer.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        Assert.Equal(HttpStatusCode.NoContent, (await operador.PostAsync($"/api/contactos/{target.Id}/baja", new BajaRequest("Pidió por teléfono"))).StatusCode);
        var detail = (await operador.Client.GetFromJsonAsync<ContactoDetalleDto>(Rel($"/api/contactos/{target.Id}")))!;
        Assert.Equal("OptOut", detail.Contacto.Consentimiento);
        Assert.Equal("Intranet", Assert.Single(detail.Historial).Source);
    }

    private static Uri Rel(string path) => new(path, UriKind.Relative);

    private static async Task<HttpResponseMessage> ImportAsync(ApiSession session, string csv, bool withXsrf)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "archivo", "clientes.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, Rel("/api/contactos/importar")) { Content = form };
        if (withXsrf)
        {
            request.Headers.Add(AuthServiceCollectionExtensions.XsrfHeaderName, session.Xsrf);
        }

        return await session.Client.SendAsync(request);
    }

    private async Task<ApiSession> SessionAsync(string userName, string role)
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = userName };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var session = new ApiSession(_factory.CreateClient());
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(userName, Password)).StatusCode);
        return session;
    }
}
