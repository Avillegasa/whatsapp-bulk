using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Contacts;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Infrastructure.Templates;

namespace WaBulk.Worker;

/// <summary>
/// Comando de prueba de F0.5: envía una plantilla a un número permitido y muestra el SendAttempt.
/// Uso: dotnet run --project src/backend/WaBulk.Worker -- send-test --to +591XXXXXXXX [--template hello_world] [--lang en_US]
///      [--param nombre=Andrés --param promocion=…]
/// F1.5: la plantilla se toma de la tabla Template (sincronizada con Meta): debe estar APPROVED y los --param deben
/// coincidir con los parámetros de su cuerpo; se envían como components.
/// Usa un DispatchJob de prueba sin campaña (CampaignVersionId NULL, F1.6) y el Contact del número (se crea si no
/// existe, F1.4), reutilizado en cada ejecución; cada ejecución crea un SendAttempt nuevo.
/// </summary>
public static partial class SendTestCommand
{
    public const string Name = "send-test";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        var to = GetOption(args, "--to");
        var templateName = GetOption(args, "--template") ?? "hello_world";
        var language = GetOption(args, "--lang");
        var values = GetParams(args);

        if (to is null || !PhoneRegex().IsMatch(to) || values is null)
        {
            await Console.Error.WriteLineAsync(
                "Uso: send-test --to +591XXXXXXXX [--template hello_world] [--lang en_US] [--param nombre=valor ...]");
            return 2;
        }

        var phone = to.StartsWith('+') ? to : "+" + to;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WaBulkDbContext>();
        var (message, error) = await TemplateMessageFactory.BuildAsync(db, templateName, language, values, cancellationToken);
        if (message is null)
        {
            await Console.Error.WriteLineAsync(error);
            return 2;
        }

        Console.WriteLine($"Plantilla     : {message.Name} ({message.LanguageCode}), parámetros: "
                          + (message.BodyParameters is { Count: > 0 } ps ? string.Join(", ", ps.Select(p => $"{p.Name}={p.Value}")) : "ninguno"));
        var sender = scope.ServiceProvider.GetRequiredService<TemplateSendService>();
        var contact = await scope.ServiceProvider.GetRequiredService<ContactDirectory>()
            .ResolveAsync(phone, null, null, $"windows:{Environment.UserName}", cancellationToken);
        var contactId = contact.Id;
        var now = DateTime.UtcNow;

        var job = await db.DispatchJobs.SingleOrDefaultAsync(
            j => j.CampaignVersionId == null && j.ContactId == contactId, cancellationToken);
        if (job is null)
        {
            job = new DispatchJob
            {
                CampaignVersionId = null,
                ContactId = contactId,
                PhoneE164 = phone,
                ScheduledUtc = now,
                ExpiresUtc = now.AddDays(1),
            };
            db.DispatchJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
        }

        var attempt = await sender.SendAsync(job, message, cancellationToken);

        // El trabajo de prueba no pasa por la cola: se deja en su estado final para que el despachador (F0.9)
        // nunca lo reclame y reenvíe. Sin reintentos: un envío de prueba fallido se repite a mano.
        job.Attempts++;
        job.DispatchStatus = attempt.Status switch
        {
            SendAttemptStatus.Accepted => DispatchStatus.Submitted,
            SendAttemptStatus.SubmissionUnknown => DispatchStatus.NeedsReview,
            _ => DispatchStatus.Failed,
        };
        await db.SaveChangesAsync(CancellationToken.None);

        Console.WriteLine($"DispatchJobId : {job.Id}");
        Console.WriteLine($"AttemptId     : {attempt.AttemptId}");
        Console.WriteLine($"Estado        : {attempt.Status}");
        Console.WriteLine($"Wamid         : {attempt.Wamid}");
        Console.WriteLine($"HttpStatus    : {attempt.HttpStatus}");
        Console.WriteLine($"CodigoError   : {attempt.ErrorCode}");
        Console.WriteLine($"Detalle       : {attempt.Detail}");
        return attempt.Status == SendAttemptStatus.Accepted ? 0 : 1;
    }

    /// <summary>--param nombre=valor (repetible). null si alguno no tiene "=" o se repite.</summary>
    private static Dictionary<string, string>? GetParams(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--param")
            {
                continue;
            }

            var pair = args[++i];
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !values.TryAdd(pair[..eq].Trim(), pair[(eq + 1)..]))
            {
                return null;
            }
        }

        return values;
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [GeneratedRegex(@"^\+?[1-9]\d{7,14}$")]
    private static partial Regex PhoneRegex();
}
