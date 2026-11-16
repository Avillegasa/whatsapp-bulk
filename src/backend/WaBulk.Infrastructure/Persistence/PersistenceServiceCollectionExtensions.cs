using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WaBulk.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "WaBulk";

    /// <summary>
    /// Registra <see cref="WaBulkDbContext"/>. La cadena de conexión se valida al crear el contexto,
    /// no al arrancar, para que /health responda aunque falte. En desarrollo va en dotnet user-secrets.
    /// </summary>
    public static IServiceCollection AddWaBulkPersistence(this IServiceCollection services, string? connectionString)
    {
        services.AddDbContext<WaBulkDbContext>(options =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Falta la cadena de conexión 'ConnectionStrings:{ConnectionStringName}'.");
            }

            options.UseSqlServer(connectionString);
        });
        return services;
    }
}
