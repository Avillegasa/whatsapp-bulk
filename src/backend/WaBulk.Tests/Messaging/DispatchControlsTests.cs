using System.Diagnostics;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Messaging;

namespace WaBulk.Tests.Messaging;

/// <summary>Controles del despachador sin base de datos (F1.7): horario permitido y tasa por número.</summary>
public sealed class DispatchControlsTests
{
    private static readonly SendingWindow LaPaz = SendingWindow.Parse("08:00", "20:00", "America/La_Paz");

    [Theory]
    [InlineData("2026-11-18T11:59:00", false)] // 07:59 en La Paz (UTC−4)
    [InlineData("2026-11-18T12:00:00", true)]  // 08:00
    [InlineData("2026-11-18T23:59:00", true)]  // 19:59
    [InlineData("2026-11-19T00:00:00", false)] // 20:00: el límite superior no entra
    [InlineData("2026-11-19T05:00:00", false)] // 01:00
    public void Horario_de_La_Paz(string utc, bool open)
    {
        Assert.Equal(open, LaPaz.IsOpen(DateTime.SpecifyKind(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)));
    }

    [Fact]
    public void Horario_que_cruza_la_medianoche_y_todo_el_dia()
    {
        var night = SendingWindow.Parse("22:00", "06:00", "UTC");
        Assert.True(night.IsOpen(new DateTime(2026, 11, 18, 23, 0, 0, DateTimeKind.Utc)));
        Assert.True(night.IsOpen(new DateTime(2026, 11, 18, 5, 59, 0, DateTimeKind.Utc)));
        Assert.False(night.IsOpen(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc)));
        Assert.True(SendingWindow.Parse("00:00", "00:00", "America/La_Paz").IsOpen(new DateTime(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Token_bucket_espacia_los_envios_segun_la_tasa()
    {
        var clock = new SteppingClock();
        var limiter = new SendRateLimiter(Options.Create(new DispatcherOptions { MessagesPerSecond = 10, RateBurst = 1 }), clock);

        // 21 envíos pedidos en el mismo instante: el primero sale ya y cada uno de los siguientes 100 ms después.
        var waits = Enumerable.Range(0, 21).Select(_ => limiter.Reserve()).ToList();
        Assert.Equal(TimeSpan.Zero, waits[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), waits[1]);
        Assert.Equal(TimeSpan.FromSeconds(2), waits[20]);

        // Pasados 2,1 s se pagó la deuda y vuelve a haber una ficha (no se acumulan más de RateBurst).
        clock.Advance(TimeSpan.FromSeconds(2.1));
        Assert.Equal(TimeSpan.Zero, limiter.Reserve());
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.Zero, limiter.Reserve());
        Assert.Equal(TimeSpan.FromMilliseconds(100), limiter.Reserve());
    }

    [Fact]
    public async Task Tasa_medida_con_el_reloj_real()
    {
        var limiter = new SendRateLimiter(Options.Create(new DispatcherOptions { MessagesPerSecond = 20, RateBurst = 1 }), TimeProvider.System);
        var watch = Stopwatch.StartNew();

        for (var i = 0; i < 11; i++)
        {
            await limiter.WaitAsync(CancellationToken.None);
        }

        // 11 envíos a 20/s: al menos 10 × 50 ms. Tasa medida ≤ 20/s (con margen por la resolución del timer).
        watch.Stop();
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(480), $"tardó {watch.Elapsed.TotalMilliseconds} ms");
        Assert.True(10 / watch.Elapsed.TotalSeconds <= 21, $"tasa {10 / watch.Elapsed.TotalSeconds:0.0}/s");
    }

    [Fact]
    public void Tope_semanal_igual_en_la_Api_y_en_el_Worker()
    {
        // D-031: la Api escribe {N} en el texto de la oferta y el Worker lo hace cumplir; deben ser el mismo número.
        var dir = new DirectoryInfo(Path.GetDirectoryName(SourceFile())!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WaBulk.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        int Read(string project)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, project, "appsettings.json")));
            return doc.RootElement.GetProperty("OfertaPromociones").GetProperty("MaxPromotionsPerWeek").GetInt32();
        }

        Assert.Equal(Read("WaBulk.Api"), Read("WaBulk.Worker"));
    }

    [Fact]
    public void Capacidad_igual_en_la_Api_y_en_el_Worker()
    {
        // F1.8: el tablero de la Api muestra la capacidad con la misma configuración que usa el despachador del Worker.
        var dir = new DirectoryInfo(Path.GetDirectoryName(SourceFile())!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WaBulk.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string Read(string project, string key)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, project, "appsettings.json")));
            return doc.RootElement.GetProperty("Capacity").GetProperty(key).ToString();
        }

        foreach (var key in new[] { "PortfolioId", "PortfolioLimit", "UsagePercent", "ManualQuota" })
        {
            Assert.Equal(Read("WaBulk.Worker", key), Read("WaBulk.Api", key));
        }
    }

    /// <summary>Ruta de este archivo al compilar: sirve aunque las pruebas corran desde otra carpeta (--artifacts-path).</summary>
    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    /// <summary>Reloj manual también para GetTimestamp (lo usa el token bucket).</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now.UtcTicks;

        public void Advance(TimeSpan by) => _now += by;
    }
}
