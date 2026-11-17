using WaBulk.Api.Auth;
using WaBulk.Api.Campaigns;
using WaBulk.Api.Contacts;
using WaBulk.Api.Templates;
using WaBulk.Api.Users;
using WaBulk.Infrastructure.Campaigns;
using WaBulk.Infrastructure.Contacts;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Infrastructure.Templates;

// Comando de una sola ejecución: crear el primer Admin (no levanta el servidor web).
var command = args.Length > 0 && args[0] == CreateAdminCommand.Name ? args[0] : null;

// En modo comando no se pasan los argumentos al host para que no se lean como configuración.
var builder = WebApplication.CreateBuilder(command is null ? args : []);

builder.Services.AddHealthChecks();
builder.Services.AddWaBulkPersistence(
    builder.Configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName));
builder.Services.AddWaBulkAuth(builder.Environment);

// F1.4: la oferta de promociones se envía desde la Api (botón "Ofrecer promociones"). IDs de Meta en appsettings;
// el token en user-secrets (almacén compartido, D-018) o META__ACCESSTOKEN en Docker.
builder.Services.AddMetaWhatsApp(builder.Configuration);
builder.Services.AddContacts(builder.Configuration);
// F1.5: plantillas (lista, sincronización con Meta y creación).
builder.Services.AddTemplates(builder.Configuration);
// F1.6: campañas versionadas y aprobación (los trabajos se crean al aprobar; los envía el Worker).
builder.Services.AddCampaigns(builder.Configuration);

var app = builder.Build();

if (command is not null)
{
    return await CreateAdminCommand.RunAsync(app.Services, args[1..]);
}

app.UseAuthentication();
app.UseAuthorization();

// Liveness: responde "Healthy" si el proceso está arriba.
app.MapHealthChecks("/health").AllowAnonymous();

// Toda la API bajo /api, con validación XSRF en los métodos que cambian algo.
app.MapGroup("/api")
    .AddEndpointFilter<XsrfValidationFilter>()
    .MapAuthEndpoints()
    .MapUsersEndpoints()
    .MapContactsEndpoints()
    .MapTemplatesEndpoints()
    .MapCampaignsEndpoints();

await app.RunAsync();
return 0;
