using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Worker;

/// <summary>
/// Comando de prueba de F0.5: envía una plantilla a un número permitido y muestra el SendAttempt.
/// Uso: dotnet run --project src/backend/WaBulk.Worker -- send-test --to +591XXXXXXXX [--template hello_world] [--lang en_US]
/// Usa un DispatchJob de prueba con CampaignVersionId = 0 (no existe campaña 0) y ContactId = dígitos del número,
/// reutilizado en cada ejecución; cada ejecución crea un SendAttempt nuevo.
/// </summary>
public static partial class SendTestCommand
{
    public const string Name = "send-test";
    private const long TestCampaignVersionId = 0;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        var to = GetOption(args, "--to");
        var templateName = GetOption(args, "--template") ?? "hello_world";
        var language = GetOption(args, "--lang") ?? "en_US";

        if (to is null || !PhoneRegex().IsMatch(to))
        {
            await Console.Error.WriteLineAsync("Uso: send-test --to +591XXXXXXXX [--template hello_world] [--lang en_US]");
            return 2;
        }

        var phone = to.StartsWith('+') ? to : "+" + to;
        var contactId = long.Parse(phone[1..], System.Globalization.CultureInfo.InvariantCulture);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WaBulkDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<TemplateSendService>();
        var now = DateTime.UtcNow;

        var job = await db.DispatchJobs.SingleOrDefaultAsync(
            j => j.CampaignVersionId == TestCampaignVersionId && j.ContactId == contactId, cancellationToken);
        if (job is null)
        {
            job = new DispatchJob
            {
                CampaignVersionId = TestCampaignVersionId,
                ContactId = contactId,
                PhoneE164 = phone,
                ScheduledUtc = now,
                ExpiresUtc = now.AddDays(1),
            };
            db.DispatchJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
        }

        var attempt = await sender.SendAsync(job, new TemplateMessage(templateName, language), cancellationToken);

        Console.WriteLine($"DispatchJobId : {job.Id}");
        Console.WriteLine($"AttemptId     : {attempt.AttemptId}");
        Console.WriteLine($"Estado        : {attempt.Status}");
        Console.WriteLine($"Wamid         : {attempt.Wamid}");
        Console.WriteLine($"HttpStatus    : {attempt.HttpStatus}");
        Console.WriteLine($"CodigoError   : {attempt.ErrorCode}");
        Console.WriteLine($"Detalle       : {attempt.Detail}");
        return attempt.Status == SendAttemptStatus.Accepted ? 0 : 1;
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [GeneratedRegex(@"^\+?[1-9]\d{7,14}$")]
    private static partial Regex PhoneRegex();
}
