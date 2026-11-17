using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using WaBulk.WebhookReplay;

// Uso (desde la raíz del repo, en cmd):
//   dotnet run --project tools\webhook-replay -- <escenario> [--intento ultimo|<AttemptId>] [--wamid <wamid>]
//                                                [--url <url>] [--firma-mala]
//   dotnet run --project tools\webhook-replay -- lista
// Ver tools/webhook-replay/README.md.

if (args.Length == 0 || args[0] is "lista" or "-h" or "--help")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

var scenario = ReplayScenarios.Find(args[0]);
if (scenario is null)
{
    Console.Error.WriteLine($"Escenario desconocido: {args[0]}");
    PrintUsage();
    return 1;
}

string? intento = null, wamidArg = null, urlArg = null;
var wrongSignature = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--intento" when i + 1 < args.Length: intento = args[++i]; break;
        case "--wamid" when i + 1 < args.Length: wamidArg = args[++i]; break;
        case "--url" when i + 1 < args.Length: urlArg = args[++i]; break;
        case "--firma-mala": wrongSignature = true; break;
        default:
            Console.Error.WriteLine($"Opción no reconocida: {args[i]}");
            return 1;
    }
}

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets(typeof(ReplaySender).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var appSecret = config["Meta:AppSecret"];
if (string.IsNullOrWhiteSpace(appSecret))
{
    Console.Error.WriteLine("Falta Meta:AppSecret en user-secrets (el mismo que usa el receptor, D-018).");
    return 1;
}

var replay = config.GetSection("Replay");
var url = new Uri(urlArg ?? replay["Url"] ?? "http://localhost:5244/webhooks/whatsapp");
var waId = replay["WaId"] ?? "59170000000";

// Intento a correlacionar: real (desde SQL) o sintético.
Guid attemptId;
string wamid;
if (intento is null)
{
    attemptId = Guid.CreateVersion7();
    wamid = wamidArg ?? "wamid.REPLAY" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    if (!wrongSignature && scenario.Steps.Any(s => s.Payload.StartsWith("status", StringComparison.Ordinal) || s.Payload == "lote"))
    {
        Console.WriteLine("Aviso: AttemptId y wamid sintéticos; el procesador no los encontrará (EnEspera y luego Revision).");
        Console.WriteLine("       Usa --intento ultimo para correlacionar con el último envío real.");
    }
}
else
{
    (Guid AttemptId, string? Wamid, string WaId)? row;
    try
    {
        row = await FindAttemptAsync(config.GetConnectionString("WaBulk"), intento);
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or SqlException)
    {
        Console.Error.WriteLine($"No se pudo leer el intento: {ex.Message}");
        return 1;
    }

    if (row is null)
    {
        Console.Error.WriteLine(intento == "ultimo"
            ? "No hay ningún SendAttempt con wamid. Haz antes un send-test."
            : $"No existe el SendAttempt {intento}.");
        return 1;
    }

    attemptId = row.Value.AttemptId;
    waId = row.Value.WaId;
    wamid = wamidArg ?? row.Value.Wamid ?? "wamid.REPLAY" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
}

var ctx = new ReplayContext(
    replay["WabaId"] ?? "",
    replay["PhoneNumberId"] ?? "",
    replay["DisplayPhoneNumber"] ?? "",
    waId,
    wamid,
    attemptId,
    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    "wamid.REPLAYIN" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)));

var pause = TimeSpan.FromMilliseconds(int.TryParse(replay["PauseMs"], out var ms) ? ms : 300);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var sender = new ReplaySender(http, url, appSecret, wrongSignature, pause);

Console.WriteLine($"Escenario: {scenario.Name} — {scenario.Description}");
Console.WriteLine($"Destino:   {url}{(wrongSignature ? "  (firma incorrecta)" : "")}");
Console.WriteLine($"AttemptId: {attemptId:D}");
Console.WriteLine($"Wamid:     {wamid}");

IReadOnlyList<ReplayResult> results;
try
{
    results = await sender.RunAsync(scenario, ctx);
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"No se pudo conectar con {url}: {ex.Message}");
    Console.Error.WriteLine("¿Está corriendo el receptor? dotnet run --project src\\backend\\WaBulk.Webhooks");
    return 1;
}

var ok = true;
for (var i = 0; i < results.Count; i++)
{
    var r = results[i];
    var match = r.Status == sender.ExpectedStatus;
    ok &= match;
    Console.WriteLine($"  POST {i + 1}/{results.Count} {r.Step.Label,-28} {r.Bytes,5} bytes → {(int)r.Status} {r.Status}{(match ? "" : $"  (se esperaba {(int)sender.ExpectedStatus})")}");
}

Console.WriteLine(wrongSignature
    ? "Esperado: 401 y ninguna fila nueva en WebhookInbox."
    : $"Esperado con el Worker corriendo: {scenario.Expected}");
Console.WriteLine("Revisar con db\\scripts\\verificar-f0.8.sql.");
return ok ? 0 : 2;

static async Task<(Guid AttemptId, string? Wamid, string WaId)?> FindAttemptAsync(string? connectionString, string intento)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Falta ConnectionStrings:WaBulk en user-secrets.");
    }

    Guid? id = null;
    if (intento != "ultimo")
    {
        if (!Guid.TryParse(intento, out var parsed))
        {
            throw new ArgumentException($"--intento debe ser 'ultimo' o un AttemptId: {intento}");
        }

        id = parsed;
    }

    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = id is null
        ? """
          SELECT TOP 1 a.AttemptId, a.Wamid, j.PhoneE164
          FROM SendAttempt a JOIN DispatchJob j ON j.Id = a.DispatchJobId
          WHERE a.Wamid IS NOT NULL
          ORDER BY a.InicioUtc DESC
          """
        : """
          SELECT a.AttemptId, a.Wamid, j.PhoneE164
          FROM SendAttempt a JOIN DispatchJob j ON j.Id = a.DispatchJobId
          WHERE a.AttemptId = @id
          """;
    if (id is not null)
    {
        cmd.Parameters.AddWithValue("@id", id.Value);
    }

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return null;
    }

    // recipient_id/wa_id van sin '+', como los manda Meta.
    return (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2).TrimStart('+'));
}

static void PrintUsage()
{
    Console.WriteLine("""
        Uso: dotnet run --project tools\webhook-replay -- <escenario> [opciones]

        Opciones:
          --intento ultimo|<AttemptId>  correlaciona con un SendAttempt real (lee SQL); sin esto, ids sintéticos
          --wamid <wamid>               usa este wamid en lugar del guardado en el intento
          --url <url>                   receptor (por defecto http://localhost:5244/webhooks/whatsapp)
          --firma-mala                  firma con otra clave: el receptor debe responder 401

        Escenarios:
        """);
    foreach (var s in ReplayScenarios.All)
    {
        Console.WriteLine($"  {s.Name,-17} {s.Description}");
    }
}
