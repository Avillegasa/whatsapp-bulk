using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Worker;

// Comandos de una sola ejecución (no levantan los servicios de fondo).
string[] commands = [SendTestCommand.Name, AlertsCommand.ListName, AlertsCommand.ResumeName, AlertsCommand.ReviewName, AlertsCommand.LimitName];
var command = args.Length > 0 && commands.Contains(args[0]) ? args[0] : null;

// En modo comando no se pasan los argumentos al host para que no se lean como configuración.
var builder = Host.CreateApplicationBuilder(command is null ? args : []);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddWaBulkPersistence(
    builder.Configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName));
builder.Services.AddMetaWhatsApp(builder.Configuration);
builder.Services.AddScoped<TemplateSendService>();
builder.Services.AddWebhookInboxProcessing(builder.Configuration);
builder.Services.AddDispatcher(builder.Configuration);

if (command is not null)
{
    using var commandHost = builder.Build();
    return command switch
    {
        SendTestCommand.Name => await SendTestCommand.RunAsync(commandHost.Services, args[1..], CancellationToken.None),
        AlertsCommand.ListName => await AlertsCommand.ListAsync(commandHost.Services, CancellationToken.None),
        AlertsCommand.ReviewName => await AlertsCommand.ReviewAsync(commandHost.Services, CancellationToken.None),
        AlertsCommand.LimitName => await AlertsCommand.LimitAsync(commandHost.Services, CancellationToken.None),
        _ => await AlertsCommand.ResumeAsync(commandHost.Services, args[1..], CancellationToken.None),
    };
}

builder.Services.AddHostedService<DispatcherWorker>();
builder.Services.AddHostedService<InboxProcessorWorker>();

using var host = builder.Build();
await host.RunAsync();
return 0;
