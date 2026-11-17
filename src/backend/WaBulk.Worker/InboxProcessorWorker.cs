using Microsoft.Extensions.Options;
using WaBulk.Infrastructure.Messaging;

namespace WaBulk.Worker;

/// <summary>
/// Ejecuta el procesador de WebhookInbox en bucle (F0.7). Si una vuelta llena el lote sigue de inmediato;
/// si no, espera PollSeconds. Un error de una vuelta se registra y no detiene el servicio.
/// </summary>
public sealed partial class InboxProcessorWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<InboxProcessorOptions> options,
    TimeProvider clock,
    ILogger<InboxProcessorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, options.Value.PollSeconds);
        var poll = TimeSpan.FromSeconds(options.Value.PollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var taken = 0;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<WebhookInboxProcessor>();
                taken = await processor.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // El servicio debe seguir vivo ante errores de una vuelta (p. ej. base caída).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBatchFailed(logger, ex);
            }

            if (taken < options.Value.BatchSize)
            {
                try
                {
                    await Task.Delay(poll, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Procesador de WebhookInbox iniciado (cada {PollSeconds} s)")]
    private static partial void LogStarted(ILogger logger, int pollSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló una vuelta del procesador de WebhookInbox")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);
}
