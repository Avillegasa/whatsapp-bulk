namespace WaBulk.Worker;

/// <summary>
/// Servicio de fondo de marcador de posición. El despachador con cola y lease llega en F0.9.
/// </summary>
public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker iniciado");
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            logger.LogDebug("Worker activo a las {Time}", DateTimeOffset.UtcNow);
        }
    }
}
