using Microsoft.EntityFrameworkCore;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Worker;

/// <summary>
/// Comandos del freno de emergencia (F0.12), hasta que exista la pantalla de la Fase 1:
///   dotnet run --project src\backend\WaBulk.Worker -- alertas
///   dotnet run --project src\backend\WaBulk.Worker -- reanudar   (registra Environment.UserName en LevantadaPor)
/// </summary>
public static class AlertsCommand
{
    public const string ListName = "alertas";
    public const string ResumeName = "reanudar";

    public static async Task<int> ListAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WaBulkDbContext>();

        var pause = await db.DispatchPauses.AsNoTracking().Where(p => p.LiftedUtc == null).FirstOrDefaultAsync(cancellationToken);
        Console.WriteLine(pause is null
            ? "Despacho: ACTIVO (sin pausa)"
            : $"Despacho: EN PAUSA desde {pause.FromUtc:yyyy-MM-dd HH:mm:ss} UTC por {pause.Reason} (alerta {pause.AlertId})");

        var open = await db.Alerts.AsNoTracking().Where(a => a.ResolvedUtc == null).OrderBy(a => a.Id).ToListAsync(cancellationToken);
        Console.WriteLine(open.Count == 0 ? "Alertas abiertas: ninguna" : $"Alertas abiertas: {open.Count}");
        foreach (var a in open)
        {
            Console.WriteLine($"  [{a.Id}] {a.Type} ({a.Key}) ×{a.Occurrences} desde {a.Source}, última {a.LastUtc:yyyy-MM-dd HH:mm:ss} UTC: {a.Message}");
        }

        return pause is null ? 0 : 1;
    }

    public static async Task<int> ResumeAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        // Quién levanta la pausa es el usuario de Windows que ejecuta el comando (LevantadaPor): no se escribe a mano.
        if (args.Length > 0)
        {
            await Console.Error.WriteLineAsync("Uso: reanudar   (sin argumentos; se registra el usuario de Windows que lo ejecuta)");
            return 2;
        }

        var by = Environment.UserName;
        if (string.IsNullOrWhiteSpace(by))
        {
            await Console.Error.WriteLineAsync("No se pudo obtener el usuario de Windows; la pausa sigue activa.");
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var (pauses, alerts) = await scope.ServiceProvider.GetRequiredService<AlertService>().ResumeAsync(by, cancellationToken);
        Console.WriteLine(pauses == 0
            ? "No había pausa activa."
            : $"Pausa levantada por {by}. Alertas resueltas: {alerts}. Si el problema sigue, el próximo envío vuelve a pausar.");
        return 0;
    }
}
