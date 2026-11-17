using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace WaBulk.Infrastructure.Meta;

/// <summary>Plantilla tal como la devuelve GET /{WABA_ID}/message_templates.</summary>
public sealed record MetaTemplate(
    string Id,
    string Name,
    string Language,
    string Status,
    string Category,
    string? QualityScore,
    string? ParameterFormat,
    string? RejectedReason,
    string? PreviousCategory,
    string? CorrectCategory,
    string? ComponentsJson);

public enum MetaCreateOutcome { Created, Rejected, Unknown }

/// <summary>Resultado de POST /{WABA_ID}/message_templates.</summary>
public sealed record MetaCreateTemplateResult(
    MetaCreateOutcome Outcome, string? Id, string? Status, string? Category, int? ErrorCode, int? ErrorSubcode, string? Detail);

/// <summary>Error de Meta al listar plantillas (sin respuesta, o respuesta de error).</summary>
public sealed class MetaApiException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>
/// API de plantillas de la WABA (F1.5). Sin reintentos automáticos, igual que /messages: una creación repetida a ciegas
/// podría crear dos plantillas (Meta lo impide por nombre e idioma, pero no se depende de eso).
/// Referencias (consultadas 2026-11-17):
/// - https://developers.facebook.com/documentation/business-messaging/whatsapp/reference/whatsapp-business-account/message-template-api
///   (GET: fields, limit, after; respuesta data[] + paging.cursors.after / paging.next. POST: name, language, category,
///   parameter_format, components → {id, status, category}).
/// - https://developers.facebook.com/documentation/business-messaging/whatsapp/templates/marketing-templates/custom-marketing-templates
/// </summary>
public sealed class MetaTemplateApiClient(HttpClient httpClient, IOptionsMonitor<MetaOptions> options)
{
    /// <summary>Campos pedidos al listar (Message Template API).</summary>
    public const string Fields =
        "id,name,language,status,category,quality_score,parameter_format,rejected_reason,previous_category,correct_category,components";

    private const int PageSize = 100;
    private const int MaxPages = 50;
    private const int MaxDetailLength = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Todas las plantillas de la WABA, recorriendo las páginas con el cursor "after".</summary>
    public async Task<IReadOnlyList<MetaTemplate>> ListAsync(CancellationToken cancellationToken)
    {
        var meta = Config();
        var result = new List<MetaTemplate>();
        string? after = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var query = $"fields={Uri.EscapeDataString(Fields)}&limit={PageSize.ToString(CultureInfo.InvariantCulture)}"
                        + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(meta.BaseUrl, $"{meta.GraphApiVersion}/{meta.WabaId}/message_templates?{query}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", meta.AccessToken);

            string text;
            int status;
            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken);
                status = (int)response.StatusCode;
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new MetaApiException($"Sin respuesta de Meta al listar plantillas ({ex.GetType().Name}).");
            }

            if (status is < 200 or >= 300)
            {
                var error = TryParse<ErrorResponse>(text)?.Error;
                throw new MetaApiException(
                    $"Meta respondió {status} al listar plantillas: {error?.ErrorUserMsg ?? error?.Message ?? Truncate(text)}", error?.Code);
            }

            var body = TryParse<ListResponse>(text) ?? throw new MetaApiException("Respuesta de Meta sin el formato esperado (data[]).");
            foreach (var t in body.Data ?? [])
            {
                if (t.Id is null || t.Name is null || t.Language is null)
                {
                    continue;
                }

                result.Add(new MetaTemplate(t.Id, t.Name, t.Language, t.Status ?? "", t.Category ?? "", t.QualityScore?.Score,
                    t.ParameterFormat, t.RejectedReason, t.PreviousCategory, t.CorrectCategory,
                    t.Components is { ValueKind: JsonValueKind.Array } c ? c.GetRawText() : null));
            }

            after = body.Paging?.Next is null ? null : body.Paging.Cursors?.After;
            if (after is null)
            {
                return result;
            }
        }

        throw new MetaApiException($"Más de {MaxPages * PageSize} plantillas: se cortó la sincronización.");
    }

    /// <summary>
    /// Crea la plantilla. Un solo POST: sin respuesta confiable → Unknown (la sincronización dirá si se creó).
    /// </summary>
    public async Task<MetaCreateTemplateResult> CreateAsync(JsonObject body, CancellationToken cancellationToken)
    {
        var meta = Config();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(meta.BaseUrl, $"{meta.GraphApiVersion}/{meta.WabaId}/message_templates"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", meta.AccessToken);
        request.Content = JsonContent.Create(body);

        int status;
        string text;
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            return new MetaCreateTemplateResult(MetaCreateOutcome.Unknown, null, null, null, null, null,
                $"Sin respuesta de Meta ({ex.GetType().Name}: {ex.Message})");
        }

        if (status is >= 200 and < 300)
        {
            var ok = TryParse<CreateResponse>(text);
            return string.IsNullOrWhiteSpace(ok?.Id)
                ? new MetaCreateTemplateResult(MetaCreateOutcome.Unknown, null, null, null, null, null, Truncate($"2xx sin id: {text}"))
                : new MetaCreateTemplateResult(MetaCreateOutcome.Created, ok.Id, ok.Status, ok.Category, null, null, null);
        }

        var error = TryParse<ErrorResponse>(text)?.Error;
        if (error?.Code is int code)
        {
            // error_user_msg trae la explicación para personas ("Content in This Language Already Exists"…).
            var detail = string.Join(" | ", new[] { error.ErrorUserTitle, error.ErrorUserMsg, error.Message, $"fbtrace_id={error.FbtraceId}" }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            return new MetaCreateTemplateResult(MetaCreateOutcome.Rejected, null, null, null, code, error.ErrorSubcode, Truncate(detail));
        }

        return status >= 500
            ? new MetaCreateTemplateResult(MetaCreateOutcome.Unknown, null, null, null, null, null, Truncate($"HTTP {status}: {text}"))
            : new MetaCreateTemplateResult(MetaCreateOutcome.Rejected, null, null, null, null, null, Truncate($"HTTP {status}: {text}"));
    }

    private MetaOptions Config()
    {
        var meta = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(meta.GraphApiVersion) || string.IsNullOrWhiteSpace(meta.WabaId)
            || string.IsNullOrWhiteSpace(meta.AccessToken))
        {
            throw new InvalidOperationException(
                "Configuración de Meta incompleta: se requieren Meta:GraphApiVersion, Meta:WabaId y Meta:AccessToken.");
        }

        return meta;
    }

    private static T? TryParse<T>(string text)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text) => text.Length <= MaxDetailLength ? text : text[..MaxDetailLength];

    private sealed record ListResponse(IReadOnlyList<TemplateItem>? Data, Paging? Paging);

    private sealed record TemplateItem(
        string? Id, string? Name, string? Language, string? Status, string? Category, QualityScore? QualityScore,
        string? ParameterFormat, string? RejectedReason, string? PreviousCategory, string? CorrectCategory, JsonElement? Components);

    private sealed record QualityScore(string? Score);

    private sealed record Paging(Cursors? Cursors, string? Next);

    private sealed record Cursors(string? Before, string? After);

    private sealed record CreateResponse(string? Id, string? Status, string? Category);

    private sealed record ErrorResponse(MetaError? Error);

    private sealed record MetaError(
        string? Message, int? Code, int? ErrorSubcode, string? ErrorUserTitle, string? ErrorUserMsg, string? FbtraceId);
}
