using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using WaBulk.Api.Users;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Alerts;
using WaBulk.Infrastructure.Monitoring;
using WaBulk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace WaBulk.Api.Monitoring;

public sealed record ContadoresDto(
    long CampanaId, string Nombre, string Estado, int? Version, int Total, int EnCola, int Enviandose, int EnEspera, int Enviados,
    int Entregados, int Leidos, int Fallidos, int Revision, int Vencidos, IReadOnlyDictionary<string, int> Omitidos);

public sealed record CapacidadDto(int Limite, string FuenteLimite, int Presupuesto, int Usados, int Disponibles);

public sealed record PausaDto(bool EnPausa, string? Motivo, DateTime? DesdeUtc, int AlertasAbiertas);

public sealed record TableroDto(PausaDto Pausa, CapacidadDto Capacidad, IReadOnlyList<ContadoresDto> Campanias);

public sealed record BajaDto(string Telefono, string? Nombre, string Capa, string Estado, string Fuente, DateTime DesdeUtc, string? Evidencia);

public sealed record RevisionDto(
    long TrabajoId, long? CampanaId, string? Campana, string Telefono, string? Nombre, Guid IntentoId, DateTime InicioUtc, bool Vencido,
    string? Detalle);

public sealed record AlertaDto(long Id, string Tipo, string Clave, string Mensaje, string? Detalle, string Origen, int Ocurrencias,
    DateTime CreadaUtc, DateTime UltimaUtc);

public sealed record AlertasDto(PausaDto Pausa, IReadOnlyList<AlertaDto> Abiertas);

public sealed record MensajeDto(string Mensaje);

/// <summary>Hub del monitor (F1.8): el servidor empuja "tablero" a los navegadores; el cliente no llama a nada.</summary>
public sealed class MonitorHub : Hub
{
    public const string Path = "/api/hubs/monitor";
    public const string Event = "tablero";
}

/// <summary>
/// Monitor y tablero (F1.8). Operador (PuedeEnviar): tablero, bajas vigentes y envíos inciertos (marcar llegó / no llegó).
/// Aprobador (PuedeAprobar): alertas y "Reanudar" el despacho, auditado con el usuario de la intranet (igual que el comando
/// reanudar del Worker, que audita como windows:usuario).
/// </summary>
public static class MonitorEndpoints
{
    public static RouteGroupBuilder MapMonitorEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/monitor", async (MonitorService monitor, CancellationToken ct) => Results.Ok(ToDto(await monitor.SnapshotAsync(ct))))
            .RequireAuthorization(Policies.PuedeEnviar);

        api.MapGet("/bajas", async (MonitorService monitor, CancellationToken ct) =>
                Results.Ok((await monitor.OptOutsAsync(ct)).Select(b =>
                    new BajaDto(b.PhoneE164, b.Name, b.Layer, b.State, b.Source, b.SinceUtc, b.Evidence))))
            .RequireAuthorization(Policies.PuedeEnviar);

        var review = api.MapGroup("/revision").RequireAuthorization(Policies.PuedeEnviar);
        review.MapGet("", async (MonitorService monitor, CancellationToken ct) =>
            Results.Ok((await monitor.ReviewAsync(ct)).Select(r => new RevisionDto(r.JobId, r.CampaignId, r.CampaignName, r.PhoneE164,
                r.ContactName, r.AttemptId, r.StartedUtc, r.Overdue, r.Detail))));
        review.MapPost("/{id:long}/llego", (long id, ClaimsPrincipal me, MonitorService monitor, CancellationToken ct) =>
            MarkAsync(monitor, Actor(me), id, arrived: true, ct));
        review.MapPost("/{id:long}/no-llego", (long id, ClaimsPrincipal me, MonitorService monitor, CancellationToken ct) =>
            MarkAsync(monitor, Actor(me), id, arrived: false, ct));

        var alerts = api.MapGroup("/alertas").RequireAuthorization(Policies.PuedeAprobar);
        alerts.MapGet("", async (MonitorService monitor, WaBulkDbContext db, CancellationToken ct) =>
        {
            var open = await db.Alerts.AsNoTracking().Where(a => a.ResolvedUtc == null).OrderBy(a => a.Id).ToListAsync(ct);
            return Results.Ok(new AlertasDto(ToDto(await monitor.PauseAsync(ct)),
                [.. open.Select(a => new AlertaDto(a.Id, a.Type.ToString(), a.Key, a.Message, a.Detail, a.Source, a.Occurrences, a.CreatedUtc, a.LastUtc))]));
        });
        alerts.MapPost("/reanudar", async (ClaimsPrincipal me, AlertService service, CancellationToken ct) =>
        {
            var (pauses, resolved) = await service.ResumeAsync(Actor(me), ct);
            return Results.Ok(new MensajeDto(pauses == 0 && resolved == 0
                ? "No había pausa ni alertas abiertas."
                : $"Despacho reanudado por {Actor(me)}: {resolved} alertas resueltas. Si el problema sigue, el próximo envío vuelve a pausar."));
        });

        return api;
    }

    public static TableroDto ToDto(MonitorSnapshot s) => new(
        ToDto(s.Pause),
        new CapacidadDto(s.Capacity.Limit, s.Capacity.LimitSource, s.Capacity.Budget, s.Capacity.Used, s.Capacity.Remaining),
        [.. s.Campaigns.Select(c => new ContadoresDto(c.CampaignId, c.Name, c.Status.ToString(), c.Version, c.Total, c.Queued, c.Sending,
            c.OnHold, c.Sent, c.Delivered, c.Read, c.Failed, c.NeedsReview, c.Expired, c.Skipped))]);

    private static PausaDto ToDto(PauseState p) => new(p.Paused, p.Reason, p.SinceUtc, p.OpenAlerts);

    private static async Task<IResult> MarkAsync(MonitorService monitor, string actor, long id, bool arrived, CancellationToken ct)
    {
        var r = await monitor.MarkReviewAsync(actor, id, arrived, ct);
        return r.Succeeded ? Results.Ok(new MensajeDto(r.Message)) : Results.Conflict(new ErrorDto(r.Error!, r.Message));
    }

    private static string Actor(ClaimsPrincipal me) => me.Identity?.Name ?? "desconocido";
}

/// <summary>
/// Empuja el tablero por SignalR (F1.8). Los cambios ocurren en el Worker (otro proceso), así que la Api recalcula cada
/// Monitor:PushSeconds y solo envía si algo cambió. Un error (por ejemplo, la base caída) se registra y no detiene la Api.
/// </summary>
public sealed partial class MonitorBroadcaster(
    IServiceScopeFactory scopes, IHubContext<MonitorHub> hub, IOptions<MonitorOptions> options, TimeProvider clock,
    ILogger<MonitorBroadcaster> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? last = null;
        var failing = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PushSeconds), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dto = MonitorEndpoints.ToDto(await scope.ServiceProvider.GetRequiredService<MonitorService>().SnapshotAsync(stoppingToken));
                var json = JsonSerializer.Serialize(dto, Json);
                if (json != last)
                {
                    await hub.Clients.All.SendAsync(MonitorHub.Event, dto, stoppingToken);
                    last = json;
                }

                failing = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // El tablero es informativo: un error no debe detener la Api.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (!failing)
                {
                    LogFailed(logger, ex);
                }

                failing = true; // Se registra una vez por racha de errores.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo calcular o enviar el tablero del monitor")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
