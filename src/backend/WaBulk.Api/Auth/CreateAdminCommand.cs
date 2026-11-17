using System.Text;
using WaBulk.Infrastructure.Identity;

namespace WaBulk.Api.Auth;

/// <summary>
/// Crea el primer Admin sin dejar la clave en el repositorio ni en el historial de cmd:
///   dotnet run --project src\backend\WaBulk.Api -- crear-admin --usuario andres
/// La clave se pide por consola sin mostrarla (dos veces). Con la entrada redirigida (pruebas, scripts) se lee la
/// primera línea de la entrada estándar. Solo funciona si todavía no hay ningún Admin.
/// </summary>
public static class CreateAdminCommand
{
    public const string Name = "crear-admin";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var userName = args.Length == 2 && args[0] == "--usuario" ? args[1].Trim() : "";
        if (userName.Length == 0)
        {
            await Console.Error.WriteLineAsync("Uso: crear-admin --usuario <nombre>   (la clave se pide por consola)");
            return 2;
        }

        string password;
        if (Console.IsInputRedirected)
        {
            password = await Console.In.ReadLineAsync() ?? "";
        }
        else
        {
            password = ReadHidden("Clave: ");
            if (ReadHidden("Repite la clave: ") != password)
            {
                await Console.Error.WriteLineAsync("Las claves no coinciden; no se creó nada.");
                return 1;
            }
        }

        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<AdminBootstrapper>()
            .CreateFirstAdminAsync(userName, password, ActorFromWindows());
        if (!result.Succeeded)
        {
            await Console.Error.WriteLineAsync("No se creó el Admin:");
            foreach (var e in result.Errors)
            {
                await Console.Error.WriteLineAsync($"  {e.Code}: {e.Description}");
            }

            return 1;
        }

        Console.WriteLine($"Admin '{userName}' creado. Ya puede iniciar sesión en la intranet.");
        return 0;
    }

    /// <summary>Quién ejecutó el comando, para AuditLog: usuario de Windows (no se escribe a mano).</summary>
    private static string ActorFromWindows() =>
        string.IsNullOrWhiteSpace(Environment.UserName) ? "windows:desconocido" : $"windows:{Environment.UserName}";

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
    }
}
