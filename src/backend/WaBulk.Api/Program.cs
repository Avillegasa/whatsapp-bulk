using WaBulk.Api.Auth;
using WaBulk.Infrastructure.Persistence;

// Comando de una sola ejecución: crear el primer Admin (no levanta el servidor web).
var command = args.Length > 0 && args[0] == CreateAdminCommand.Name ? args[0] : null;

// En modo comando no se pasan los argumentos al host para que no se lean como configuración.
var builder = WebApplication.CreateBuilder(command is null ? args : []);

builder.Services.AddHealthChecks();
builder.Services.AddWaBulkPersistence(
    builder.Configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName));
builder.Services.AddWaBulkAuth(builder.Environment);

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
    .MapAuthEndpoints();

await app.RunAsync();
return 0;
