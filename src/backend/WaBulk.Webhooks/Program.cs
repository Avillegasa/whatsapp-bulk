using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Webhooks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<MetaOptions>(builder.Configuration.GetSection(MetaOptions.SectionName));
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.SectionName));
builder.Services.AddWaBulkPersistence(
    builder.Configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName));

// Límite también en Kestrel, para cortar la lectura antes de llegar al endpoint.
var maxBodyBytes = builder.Configuration.GetSection(WebhookOptions.SectionName).Get<WebhookOptions>()?.MaxBodyBytes
    ?? new WebhookOptions().MaxBodyBytes;
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxBodyBytes);

var app = builder.Build();

// Liveness: responde "Healthy" si el proceso está arriba.
app.MapHealthChecks("/health");
app.MapWhatsAppWebhook();

app.Run();
