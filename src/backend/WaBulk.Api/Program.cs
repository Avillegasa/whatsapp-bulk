using WaBulk.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddWaBulkPersistence(
    builder.Configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName));

var app = builder.Build();

// Liveness: responde "Healthy" si el proceso está arriba.
app.MapHealthChecks("/health");

app.Run();
