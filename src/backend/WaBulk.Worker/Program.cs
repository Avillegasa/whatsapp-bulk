using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Worker;

var isSendTest = args.Length > 0 && args[0] == SendTestCommand.Name;

// En modo comando no se pasan los argumentos al host para que no se lean como configuración.
var builder = Host.CreateApplicationBuilder(isSendTest ? [] : args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddWaBulkPersistence(
    builder.Configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName));
builder.Services.AddMetaWhatsApp(builder.Configuration);
builder.Services.AddScoped<TemplateSendService>();
builder.Services.AddWebhookInboxProcessing(builder.Configuration);

if (isSendTest)
{
    using var commandHost = builder.Build();
    return await SendTestCommand.RunAsync(commandHost.Services, args[1..], CancellationToken.None);
}

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<InboxProcessorWorker>();

using var host = builder.Build();
await host.RunAsync();
return 0;
