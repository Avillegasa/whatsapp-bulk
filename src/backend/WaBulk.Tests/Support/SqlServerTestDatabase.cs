using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WaBulk.Infrastructure.Capacity;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Tests.Support;

/// <summary>
/// Base de datos SQL Server temporal para pruebas de integración (la cola necesita bloqueos reales: InMemory no
/// tiene UPDLOCK/READPAST ni transacciones). Usa el SQL Server de desarrollo (compose.dev.yml): toma la cadena de
/// WABULK_TEST_SQL o, si no está, ConnectionStrings:WaBulk de user-secrets (D-018), crea WaBulk_Test_&lt;guid&gt;,
/// aplica las migraciones y la borra al terminar. Las pruebas que la usan llevan [Trait("Category", "Integration")].
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncLifetime
{
    private const string UserSecretsId = "ef587f8e-fda6-4bf7-b215-bdc70a49c565";
    private string _masterConnectionString = "";
    private string _name = "";

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddUserSecrets(UserSecretsId).AddEnvironmentVariables().Build();
        var source = config["WABULK_TEST_SQL"] ?? config.GetConnectionString("WaBulk")
            ?? throw new InvalidOperationException(
                "Pruebas de integración: falta WABULK_TEST_SQL o ConnectionStrings:WaBulk en user-secrets. " +
                "Levanta SQL Server (deploy/docker/compose.dev.yml) o excluye con --filter Category!=Integration.");

        _name = $"WaBulk_Test_{Guid.NewGuid():N}";
        _masterConnectionString = new SqlConnectionStringBuilder(source) { InitialCatalog = "master" }.ConnectionString;
        ConnectionString = new SqlConnectionStringBuilder(source) { InitialCatalog = _name }.ConnectionString;

        await using (var master = new SqlConnection(_masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_name}]";
            await create.ExecuteNonQueryAsync();
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public WaBulkDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<WaBulkDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>Vacía las tablas entre pruebas (más rápido que crear una base por prueba).</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM MessageEvent; DELETE FROM SendAttempt; DELETE FROM DispatchJob; DELETE FROM WebhookInbox; " +
            "DELETE FROM ConsentRecord; DELETE FROM PlatformPreference; DELETE FROM CapacityReservation; DELETE FROM DispatchPause; DELETE FROM Alert;");
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];";
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>Capacidad para pruebas que no tratan de capacidad: presupuesto sin límite práctico.</summary>
public static class TestCapacity
{
    public static CapacityService Unlimited(WaBulkDbContext db) => With(db, 100_000);

    /// <summary>Presupuesto exacto: límite = presupuesto, 100 % de uso, sin cupo manual.</summary>
    public static CapacityService With(WaBulkDbContext db, int budget, string portfolio = "TEST") =>
        new(db, Microsoft.Extensions.Options.Options.Create(new CapacityOptions
        {
            PortfolioId = portfolio,
            PortfolioLimit = budget,
            UsagePercent = 100,
            ManualQuota = 0,
        }));
}

/// <summary>Reloj manual compartido por las pruebas.</summary>
public sealed class ManualClock(DateTime startUtc) : TimeProvider
{
    private DateTimeOffset _now = new(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));

    public override DateTimeOffset GetUtcNow() => _now;

    public DateTime UtcNow => _now.UtcDateTime;

    public void Advance(TimeSpan by) => _now += by;
}
