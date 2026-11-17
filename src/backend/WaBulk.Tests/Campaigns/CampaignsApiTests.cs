using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaBulk.Api;
using WaBulk.Api.Campaigns;
using WaBulk.Api.Users;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Templates;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Campaigns;
using WaBulk.Infrastructure.Identity;
using WaBulk.Tests.Auth;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Campaigns;

/// <summary>F1.6 por HTTP: permisos por rol y la regla de cuatro ojos devuelve 403 aunque el creador también sea Aprobador.</summary>
[Trait("Category", "Integration")]
public sealed class CampaignsApiTests(SqlServerTestDatabase db) : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
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
    public async Task Creador_con_rol_Aprobador_recibe_403_y_otro_aprobador_aprueba()
    {
        var (templateId, segmentId) = await SeedAsync();
        var creador = await SessionAsync("ana-ops", Roles.Operador, Roles.Aprobador); // Tiene los dos roles.
        var aprobador = await SessionAsync("beto-aprueba", Roles.Aprobador);
        var soloOperador = await SessionAsync("caro-ops", Roles.Operador);

        var create = await creador.PostAsync("/api/campanias", new CampanaRequest("Promo", templateId, segmentId,
            [new ParametroDto("promocion", "Fijo", "2x1")], DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<ResultadoDto>())!.Id!.Value;
        Assert.Equal(HttpStatusCode.OK, (await creador.PostAsync($"/api/campanias/{id}/enviar")).StatusCode);

        // Rutas por rol: el Aprobador no crea campañas y el Operador no aprueba.
        Assert.Equal(HttpStatusCode.Forbidden, (await aprobador.Client.GetAsync(new Uri("/api/campanias", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await soloOperador.Client.GetAsync(new Uri("/api/aprobaciones", UriKind.Relative))).StatusCode);

        var detail = (await aprobador.Client.GetFromJsonAsync<CampanaDetalleDto>(new Uri($"/api/aprobaciones/{id}", UriKind.Relative)))!;
        var version = detail.Versiones[0];

        var own = await creador.PostAsync($"/api/aprobaciones/{id}/aprobar", new DecisionRequest(version.Id, version.Hash, null));
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal(CampaignErrors.Prohibido, (await own.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        var ok = await aprobador.PostAsync($"/api/aprobaciones/{id}/aprobar", new DecisionRequest(version.Id, version.Hash, null));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var again = await aprobador.PostAsync($"/api/aprobaciones/{id}/aprobar", new DecisionRequest(version.Id, version.Hash, null));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode); // Ya no está PendingApproval.

        await using var check = db.CreateContext();
        Assert.Equal(1, await check.DispatchJobs.CountAsync());
    }

    private async Task<(long TemplateId, long SegmentId)> SeedAsync()
    {
        await using var ctx = db.CreateContext();
        var template = new Template
        {
            MetaTemplateId = "999", Name = "promo_api", Language = "es", Category = TemplateCategories.Marketing,
            Status = TemplateStatuses.Approved, ParameterFormat = "NAMED", CreatedUtc = DateTime.UtcNow, CreatedBy = "meta",
            Components = """[{"type":"BODY","text":"Esta semana {{promocion}}"}]""",
        };
        var segment = new Segment { Name = "Lista API", CreatedUtc = DateTime.UtcNow, CreatedBy = "prueba" };
        var contact = new Contact { PhoneE164 = "+59170000009", WaId = "59170000009", CreatedUtc = DateTime.UtcNow, CreatedBy = "prueba" };
        ctx.AddRange(template, segment, contact);
        await ctx.SaveChangesAsync();
        ctx.SegmentMembers.Add(new SegmentMember { SegmentId = segment.Id, ContactId = contact.Id, AddedUtc = DateTime.UtcNow, AddedBy = "prueba" });
        ctx.ConsentRecords.Add(new ConsentRecord
        {
            ContactId = contact.Id, PhoneE164 = contact.PhoneE164, Status = ConsentStatus.OptIn, Source = ConsentSource.Boton,
            RecordedUtc = DateTime.UtcNow.AddDays(-1), RecordedBy = "webhook",
        });
        await ctx.SaveChangesAsync();
        return (template.Id, segment.Id);
    }

    private async Task<ApiSession> SessionAsync(string userName, params string[] roles)
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = userName };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRolesAsync(user, roles)).Succeeded);
        }

        var session = new ApiSession(_factory.CreateClient());
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(userName, Password)).StatusCode);
        return session;
    }
}
