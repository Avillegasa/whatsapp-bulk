using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Consent;

/// <summary>Configuración de la sección "Bajas": cómo se reconoce una baja en un mensaje entrante.</summary>
public sealed class OptOutOptions
{
    public const string SectionName = "Bajas";

    private static readonly string[] DefaultKeywords = ["BAJA", "STOP", "SALIR", "DETENER", "CANCELAR"];
    private static readonly string[] DefaultButtonPayloads = ["BAJA_PROMOCIONES"];
    private static readonly string[] DefaultButtonTexts = ["No recibir más"];

    // Vacías por defecto: el enlazador de configuración agrega a las colecciones ya cargadas en vez de reemplazarlas.
    public string[] Keywords { get; set; } = [];
    public string[] ButtonPayloads { get; set; } = [];
    public string[] ButtonTexts { get; set; } = [];

    public IReadOnlyList<string> EffectiveKeywords => Keywords.Length > 0 ? Keywords : DefaultKeywords;
    public IReadOnlyList<string> EffectiveButtonPayloads => ButtonPayloads.Length > 0 ? ButtonPayloads : DefaultButtonPayloads;
    public IReadOnlyList<string> EffectiveButtonTexts => ButtonTexts.Length > 0 ? ButtonTexts : DefaultButtonTexts;
}

/// <summary>
/// Consentimiento (capa empresa) y preferencias de plataforma (regla 8). Se consulta al encolar y al enviar.
/// </summary>
public sealed class ConsentService(WaBulkDbContext db, TimeProvider clock)
{
    public async Task<EligibilityResult> EvaluateAsync(string phoneE164, CancellationToken cancellationToken)
    {
        var company = await db.ConsentRecords.AsNoTracking()
            .Where(c => c.PhoneE164 == phoneE164 && c.Purpose == ConsentPurposes.Promociones)
            .OrderByDescending(c => c.RecordedUtc).ThenByDescending(c => c.Id)
            .Select(c => (ConsentStatus?)c.Status)
            .FirstOrDefaultAsync(cancellationToken);

        var platform = await db.PlatformPreferences.AsNoTracking()
            .Where(p => p.PhoneE164 == phoneE164 && p.Category == PlatformCategories.MarketingMessages)
            .OrderByDescending(p => p.RecordedUtc).ThenByDescending(p => p.Id)
            .Select(p => (PlatformPreferenceValue?)p.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return MarketingEligibility.Evaluate(company, platform);
    }

    /// <summary>
    /// Registra un cambio de la capa empresa (intranet, manual, importación). Devuelve false y no guarda si es una
    /// importación que intenta dar OptIn a quien tiene una baja (una importación nunca borra una baja).
    /// </summary>
    public async Task<bool> RecordCompanyAsync(
        string phoneE164, ConsentStatus status, ConsentSource source, string? evidence, string recordedBy,
        CancellationToken cancellationToken)
    {
        if (status == ConsentStatus.OptIn)
        {
            var current = await db.ConsentRecords.AsNoTracking()
                .Where(c => c.PhoneE164 == phoneE164 && c.Purpose == ConsentPurposes.Promociones)
                .OrderByDescending(c => c.RecordedUtc).ThenByDescending(c => c.Id)
                .Select(c => (ConsentStatus?)c.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (!MarketingEligibility.CanRecordOptIn(current, source))
            {
                return false;
            }
        }

        db.ConsentRecords.Add(new ConsentRecord
        {
            PhoneE164 = phoneE164,
            Status = status,
            Source = source,
            Evidence = evidence,
            RecordedUtc = clock.GetUtcNow().UtcDateTime,
            RecordedBy = recordedBy,
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>wa_id o recipient_id de Meta ("59170000000") → E.164 ("+59170000000").</summary>
    public static string? ToE164(string? waId) =>
        string.IsNullOrWhiteSpace(waId) ? null : waId.StartsWith('+') ? waId : "+" + waId;
}

/// <summary>Pedido de encolado de un destinatario (en la Fase 1 lo usa la aprobación de la campaña).</summary>
public sealed record EnqueueRequest(
    long CampaignVersionId, long ContactId, string PhoneE164, DateTime ScheduledUtc, DateTime ExpiresUtc,
    int Priority = 0, string? Parameters = null);

/// <summary>Crea el DispatchJob revisando bajas al encolar: si no es elegible queda Skipped con su motivo (sin POST).</summary>
public sealed class EnqueueService(WaBulkDbContext db, ConsentService consent)
{
    public async Task<DispatchJob> EnqueueAsync(EnqueueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var eligibility = await consent.EvaluateAsync(request.PhoneE164, cancellationToken);
        var job = new DispatchJob
        {
            CampaignVersionId = request.CampaignVersionId,
            ContactId = request.ContactId,
            PhoneE164 = request.PhoneE164,
            ScheduledUtc = request.ScheduledUtc,
            ExpiresUtc = request.ExpiresUtc,
            Priority = request.Priority,
            Parameters = request.Parameters,
            DispatchStatus = eligibility.Eligible ? DispatchStatus.Queued : DispatchStatus.Skipped,
            SkipReason = eligibility.SkipReason,
        };
        db.DispatchJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }
}
