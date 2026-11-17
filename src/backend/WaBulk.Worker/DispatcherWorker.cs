using Microsoft.Extensions.Options;
using WaBulk.Infrastructure.Capacity;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Meta;

namespace WaBulk.Worker;

/// <summary>
/// Ejecuta el despachador en bucle (F0.9) si Dispatcher:Enabled = true. Si una vuelta llena el lote sigue de
/// inmediato; si no, espera PollSeconds. Un error de una vuelta se registra y no detiene el servicio: lo que quedó
/// reclamado se recupera cuando vence el lease.
/// </summary>
public sealed partial class DispatcherWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DispatcherOptions> options,
    IOptions<CapacityOptions> capacityOptions,
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

        LogStarted(logger, identity.Owner, options.Value.PollSeconds);
        var poll = TimeSpan.FromSeconds(options.Value.PollSeconds);
        var pausedLogged = false;
        var closedLogged = false;
        var nextLimitRefresh = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            if (clock.GetUtcNow().UtcDateTime >= nextLimitRefresh)
            {
                // F1.7: límite real de Meta al arrancar y cada Capacity:RefreshHours (si falla, sigue con el último conocido).
                nextLimitRefresh = clock.GetUtcNow().UtcDateTime.AddHours(capacityOptions.Value.RefreshHours);
                await RefreshLimitAsync(stoppingToken);
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<Dispatcher>();
                var round = await dispatcher.RunOnceAsync(stoppingToken);
                claimed = round.Claimed;
                if (round.Paused && !pausedLogged)
                {
                    LogPaused(logger);
                }

                pausedLogged = round.Paused;
                if (round.OutsideHours != closedLogged)
                {
                    if (round.OutsideHours)
                    {
                        LogClosed(logger, options.Value.AllowedFrom, options.Value.AllowedUntil, options.Value.TimeZone);
                    }
                    else
                    {
                        LogOpened(logger);
                    }
                }

                closedLogged = round.OutsideHours;
                if (round.Claimed + round.Recovered + round.Expired > 0)
                {
                    LogRound(logger, round.Recovered, round.Expired, round.Claimed, round.Sent, round.Skipped, round.NoCapacity, round.LeaseLost);
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

    private async Task RefreshLimitAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var value = await scope.ServiceProvider.GetRequiredService<MessagingLimitService>()
                .RefreshFromApiAsync(scope.ServiceProvider.GetRequiredService<MetaWhatsAppClient>(), stoppingToken);
            LogLimit(logger, value ?? "(sin dato)");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Leer el límite es un extra: un error no debe detener el despachador.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRoundFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Límite de mensajería de Meta: {Value}")]
    private static partial void LogLimit(ILogger logger, string value);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fuera del horario permitido ({From}–{Until}, {Zone}): no se envía hasta que abra")]
    private static partial void LogClosed(ILogger logger, string from, string until, string zone);

    [LoggerMessage(Level = LogLevel.Information, Message = "Horario permitido abierto: el despacho sigue")]
    private static partial void LogOpened(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Despachador apagado (Dispatcher:Enabled = false)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Despachador {Owner} iniciado (cada {PollSeconds} s; plantilla y parámetros de cada versión de campaña)")]
    private static partial void LogStarted(ILogger logger, string owner, int pollSeconds);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Vuelta: recuperados={Recovered} vencidos={Expired} reclamados={Claimed} enviados={Sent} omitidos={Skipped} sin capacidad={NoCapacity} sin lease={LeaseLost}")]
    private static partial void LogRound(ILogger logger, int recovered, int expired, int claimed, int sent, int skipped, int noCapacity, int leaseLost);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Despacho en pausa global: no se reclaman trabajos (ver alertas; levantar con el comando reanudar)")]
    private static partial void LogPaused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló una vuelta del despachador")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
