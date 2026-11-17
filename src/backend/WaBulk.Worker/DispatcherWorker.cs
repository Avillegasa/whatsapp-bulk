using Microsoft.Extensions.Options;
using WaBulk.Infrastructure.Messaging;

namespace WaBulk.Worker;

/// <summary>
/// Ejecuta el despachador en bucle (F0.9) si Dispatcher:Enabled = true. Si una vuelta llena el lote sigue de
/// inmediato; si no, espera PollSeconds. Un error de una vuelta se registra y no detiene el servicio: lo que quedó
/// reclamado se recupera cuando vence el lease.
/// </summary>
public sealed partial class DispatcherWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DispatcherOptions> options,
    DispatcherIdentity identity,
    TimeProvider clock,
    ILogger<DispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        LogStarted(logger, identity.Owner, options.Value.PollSeconds, options.Value.TemplateName);
        var poll = TimeSpan.FromSeconds(options.Value.PollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<Dispatcher>();
                var round = await dispatcher.RunOnceAsync(stoppingToken);
                claimed = round.Claimed;
                if (round.Claimed + round.Recovered + round.Expired > 0)
                {
                    LogRound(logger, round.Recovered, round.Expired, round.Claimed, round.Sent, round.Skipped, round.LeaseLost);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // El servicio debe seguir vivo ante errores de una vuelta (p. ej. base caída).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRoundFailed(logger, ex);
            }

            if (claimed < options.Value.BatchSize)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Despachador apagado (Dispatcher:Enabled = false)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Despachador {Owner} iniciado (cada {PollSeconds} s, plantilla {Template})")]
    private static partial void LogStarted(ILogger logger, string owner, int pollSeconds, string template);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Vuelta: recuperados={Recovered} vencidos={Expired} reclamados={Claimed} enviados={Sent} omitidos={Skipped} sin lease={LeaseLost}")]
    private static partial void LogRound(ILogger logger, int recovered, int expired, int claimed, int sent, int skipped, int leaseLost);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló una vuelta del despachador")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
