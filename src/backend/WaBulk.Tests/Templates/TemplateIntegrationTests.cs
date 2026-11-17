using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Messaging;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Persistence;
using WaBulk.Infrastructure.Templates;
using WaBulk.Tests.Meta;
using WaBulk.Tests.Support;
using WaBulk.WebhookReplay;

namespace WaBulk.Tests.Templates;

/// <summary>
/// Plantillas (F1.5) contra SQL Server: creación con la fila y la auditoría confirmadas antes del POST, sincronización
/// con Meta y webhooks de estado, calidad y categoría. Los webhooks son los mismos payloads de tools/webhook-replay
/// (escenarios plantilla-*), con la forma de la referencia de Meta.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TemplateIntegrationTests(SqlServerTestDatabase database)
    : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private const long MetaId = 1501960108463090;
    private static readonly DateTime Start = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long StartUnix = new DateTimeOffset(Start).ToUnixTimeSeconds();
    private readonly ManualClock _clock = new(Start);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Creación ----------

    [Fact]
    public async Task Crear_confirma_fila_y_auditoria_antes_del_POST_y_queda_PENDING()
    {
        (string? Status, int Audits)? seenAtPost = null;
        var handler = new FakeHttpHandler(async (_, _) =>
        {
            await using var check = database.CreateContext(); // Otra conexión: solo ve lo confirmado.
            seenAtPost = ((await check.Templates.SingleOrDefaultAsync())?.Status, await check.AuditLogs.CountAsync());
            return MetaWhatsAppClientTests.Json(HttpStatusCode.OK, $$"""{"id":"{{MetaId}}","status":"PENDING","category":"MARKETING"}""");
        });

        var result = await CreateAsync(handler, TemplateRulesTests.ValidDraft());

        Assert.True(result.Succeeded);
        Assert.Equal((TemplateStatuses.Creating, 1), seenAtPost);
        Assert.Single(handler.Requests);
        await using var db = database.CreateContext();
        var t = await db.Templates.SingleAsync();
        Assert.Equal((MetaId.ToString(), TemplateStatuses.Pending, "MARKETING", "andres"), (t.MetaTemplateId, t.Status, t.Category, t.CreatedBy));
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal((AuditActions.CrearPlantilla, "andres"), (audit.Action, audit.User));
        Assert.Contains("No recibir más", audit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalida_no_llama_a_Meta_ni_guarda()
    {
        var handler = Created();
        var result = await CreateAsync(handler, TemplateRulesTests.ValidDraft("Con Espacios"));

        Assert.Equal("Invalida", result.Error);
        Assert.Empty(handler.Requests);
        await using var db = database.CreateContext();
        Assert.Empty(await db.Templates.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Rechazada_por_Meta_queda_ERROR_CREACION_y_se_puede_reintentar()
    {
        var rejecting = new FakeHttpHandler((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"Invalid parameter","code":100,"error_subcode":2388024,"error_user_msg":"Content in This Language Already Exists"}}""")));
        var first = await CreateAsync(rejecting, TemplateRulesTests.ValidDraft());

        Assert.Equal("RechazadaPorMeta", first.Error);
        await using (var db = database.CreateContext())
        {
            var t = await db.Templates.SingleAsync();
            Assert.Equal(TemplateStatuses.CreateFailed, t.Status);
            Assert.Contains("2388024", t.StatusReason, StringComparison.Ordinal);
        }

        var second = await CreateAsync(Created(), TemplateRulesTests.ValidDraft());
        Assert.True(second.Succeeded);
        await using var after = database.CreateContext();
        Assert.Equal(TemplateStatuses.Pending, (await after.Templates.SingleAsync()).Status); // Misma fila, no una segunda.
        Assert.Equal(2, await after.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Sin_respuesta_queda_INCIERTA_sin_reintento_y_la_sincronizacion_la_resuelve()
    {
        var timeout = new FakeHttpHandler((_, _) => throw new TaskCanceledException("timeout"));
        var result = await CreateAsync(timeout, TemplateRulesTests.ValidDraft());

        Assert.True(result.Succeeded); // No es un error del operador: la respuesta lo explica.
        Assert.Single(timeout.Requests); // Un solo POST.
        await using (var db = database.CreateContext())
        {
            Assert.Equal(TemplateStatuses.Unknown, (await db.Templates.SingleAsync()).Status);
        }

        // Una INCIERTA no bloquea otra creación, pero lo normal es sincronizar: Meta sí la tenía.
        var sync = await SyncAsync(ListWith(("777", "promo_diciembre", "es", "APPROVED", "MARKETING")));

        Assert.Equal((1, 0, 1, 0), (sync.Total, sync.Added, sync.Updated, sync.Missing));
        await using var after = database.CreateContext();
        var t = await after.Templates.SingleAsync();
        Assert.Equal(("777", TemplateStatuses.Approved), (t.MetaTemplateId, t.Status));
    }

    [Fact]
    public async Task Repetida_con_estado_de_Meta_no_se_crea()
    {
        await SyncAsync(ListWith(("1", "promo_diciembre", "es", "APPROVED", "MARKETING")));
        var handler = Created();

        var result = await CreateAsync(handler, TemplateRulesTests.ValidDraft());

        Assert.Equal("Repetida", result.Error);
        Assert.Empty(handler.Requests);
    }

    // ---------- Sincronización ----------

    [Fact]
    public async Task Sincronizar_agrega_actualiza_y_marca_las_que_ya_no_estan()
    {
        await SyncAsync(ListWith(("1", "promo_demo", "es", "PENDING", "MARKETING"), ("2", "vieja", "es", "APPROVED", "MARKETING")));
        _clock.Advance(TimeSpan.FromMinutes(5));

        var sync = await SyncAsync(ListWith(("1", "promo_demo", "es", "APPROVED", "MARKETING"), ("3", "hello_world", "en_US", "APPROVED", "UTILITY")));

        Assert.Equal((2, 1, 1, 1), (sync.Total, sync.Added, sync.Updated, sync.Missing));
        await using var db = database.CreateContext();
        var all = await db.Templates.OrderBy(t => t.MetaTemplateId).ToListAsync();
        Assert.Equal([("1", "APPROVED"), ("2", TemplateStatuses.NotFound), ("3", "APPROVED")], all.Select(t => (t.MetaTemplateId!, t.Status)));
        Assert.All(all, t => Assert.Equal(TemplateService.MetaActor, t.CreatedBy));
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.SincronizarPlantillas));
    }

    // ---------- Webhooks (payloads de tools/webhook-replay) ----------

    [Fact]
    public async Task APPROVED_por_webhook_actualiza_la_plantilla_creada()
    {
        await CreateAsync(Created(), TemplateRulesTests.ValidDraft("promo_demo"));

        await ProcessAsync(Webhook("template-approved"));

        await using var db = database.CreateContext();
        var t = await db.Templates.SingleAsync();
        Assert.Equal((TemplateStatuses.Approved, "APPROVED", Start), (t.Status, t.LastEvent, t.StatusUtc));
        Assert.Equal(WebhookInboxStatus.Procesado, (await db.WebhookInbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task REJECTED_guarda_motivo_y_recomendacion()
    {
        await CreateAsync(Created(), TemplateRulesTests.ValidDraft("promo_demo"));

        await ProcessAsync(Webhook("template-rejected"));

        await using var db = database.CreateContext();
        var t = await db.Templates.SingleAsync();
        Assert.Equal(TemplateStatuses.Rejected, t.Status);
        Assert.Contains("INVALID_FORMAT", t.StatusReason, StringComparison.Ordinal);
        Assert.Contains("Recomendación: Separate parameters", t.StatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_repetido_y_desordenado_no_retrocede_el_estado()
    {
        await CreateAsync(Created(), TemplateRulesTests.ValidDraft("promo_demo"));

        // APPROVED (T+60) llega dos veces y después un PENDING viejo (T+30).
        await ProcessAsync(Webhook("template-approved", 60), Webhook("template-approved", 60));
        await ProcessAsync(Webhook("template-approved", 30).Replace("\"APPROVED\"", "\"PENDING\"", StringComparison.Ordinal));

        await using var db = database.CreateContext();
        var t = await db.Templates.SingleAsync();
        Assert.Equal((TemplateStatuses.Approved, Start.AddSeconds(60)), (t.Status, t.StatusUtc));
        Assert.All(await db.WebhookInbox.ToListAsync(), r => Assert.Equal(WebhookInboxStatus.Procesado, r.Status));
    }

    [Fact]
    public async Task Calidad_y_categoria_por_webhook()
    {
        await CreateAsync(Created(), TemplateRulesTests.ValidDraft("promo_demo"));

        await ProcessAsync(Webhook("template-quality"));
        await ProcessAsync(Webhook("template-category"));
        await ProcessAsync(Webhook("template-quality")); // Repetido: no cambia nada.

        await using var db = database.CreateContext();
        var t = await db.Templates.SingleAsync();
        Assert.Equal(("YELLOW", Start), (t.Quality, t.QualityUtc));
        Assert.Equal(("UTILITY", "MARKETING"), (t.Category, t.PreviousCategory));
    }

    [Fact]
    public async Task Recategorizacion_anunciada_guarda_la_categoria_futura()
    {
        await CreateAsync(Created(), TemplateRulesTests.ValidDraft("promo_demo"));
        var announced = $$$"""
            {"object":"whatsapp_business_account","entry":[{"id":"2088365445132575","time":{{{StartUnix}}},"changes":[{"field":"template_category_update",
             "value":{"message_template_id":{{{MetaId}}},"message_template_name":"promo_demo","message_template_language":"es",
             "new_category":"MARKETING","correct_category":"UTILITY","category_update_timestamp":{{{StartUnix + 86_400}}}}}]}]}
            """;

        await ProcessAsync(announced);

        await using var db = database.CreateContext();
        var t = await db.Templates.SingleAsync();
        Assert.Equal(("MARKETING", "UTILITY", Start.AddDays(1)), (t.Category, t.CorrectCategory, t.CategoryChangeUtc));
    }

    [Fact]
    public async Task Estado_de_una_plantilla_desconocida_la_agrega_y_calidad_se_ignora()
    {
        await ProcessAsync(Webhook("template-approved"));
        await ProcessAsync(Webhook("template-quality", name: "otra_plantilla", id: 42));

        await using var db = database.CreateContext();
        var t = await db.Templates.SingleAsync();
        Assert.Equal((MetaId.ToString(), "promo_demo", "es", TemplateStatuses.Approved, "MARKETING", TemplateService.MetaActor),
            (t.MetaTemplateId, t.Name, t.Language, t.Status, t.Category, t.CreatedBy));
        Assert.All(await db.WebhookInbox.ToListAsync(), r => Assert.Equal(WebhookInboxStatus.Procesado, r.Status));
    }

    // ---------- Mensaje para send-test ----------

    [Fact]
    public async Task Mensaje_exige_APPROVED_y_los_parametros_exactos()
    {
        await CreateAsync(Created(), TemplateRulesTests.ValidDraft("promo_demo"));
        await using var db = database.CreateContext();
        var values = new Dictionary<string, string> { ["nombre"] = "Ana", ["promocion"] = "2x1" };

        var (pending, pendingError) = await TemplateMessageFactory.BuildAsync(db, "promo_demo", null, values, CancellationToken.None);
        Assert.Null(pending);
        Assert.Contains("PENDING", pendingError, StringComparison.Ordinal);

        await ProcessAsync(Webhook("template-approved"));
        var (_, missing) = await TemplateMessageFactory.BuildAsync(db, "promo_demo", "es",
            new Dictionary<string, string> { ["nombre"] = "Ana", ["otro"] = "x" }, CancellationToken.None);
        Assert.Contains("Faltan: promocion", missing, StringComparison.Ordinal);
        Assert.Contains("Sobran: otro", missing, StringComparison.Ordinal);

        var (message, error) = await TemplateMessageFactory.BuildAsync(db, "promo_demo", "es", values, CancellationToken.None);
        Assert.Null(error);
        Assert.True(message!.NamedParameters);
        Assert.Equal([("nombre", "Ana"), ("promocion", "2x1")], message.BodyParameters!.Select(p => (p.Name, p.Value)));
    }

    // ---------- Apoyo ----------

    private static FakeHttpHandler Created() => new((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
        $$"""{"id":"{{MetaId}}","status":"PENDING","category":"MARKETING"}""")));

    private static FakeHttpHandler ListWith(params (string Id, string Name, string Lang, string Status, string Category)[] templates) =>
        new((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                data = templates.Select(t => new { id = t.Id, name = t.Name, language = t.Lang, status = t.Status, category = t.Category }),
                paging = new { cursors = new { before = "A", after = "B" } },
            }))));

    private TemplateService Service(WaBulkDbContext db, FakeHttpHandler handler) =>
        new(db, TemplateRulesTests.TemplateClient(handler), Options.Create(new TemplateOptions()), Options.Create(new OptOutOptions()),
            _clock, NullLogger<TemplateService>.Instance);

    private async Task<TemplateCreateResult> CreateAsync(FakeHttpHandler handler, TemplateDraft draft)
    {
        await using var db = database.CreateContext();
        return await Service(db, handler).CreateAsync("andres", draft, CancellationToken.None);
    }

    private async Task<TemplateSyncResult> SyncAsync(FakeHttpHandler handler)
    {
        await using var db = database.CreateContext();
        return await Service(db, handler).SyncAsync("andres", CancellationToken.None);
    }

    private static string Webhook(string payload, long offset = 0, string name = "promo_demo", long id = MetaId) =>
        Encoding.UTF8.GetString(PayloadTemplates.Render(payload,
            new ReplayContext("2088365445132575", "1524289747428097", "15551892694", "59170000000", "wamid.X", Guid.Empty,
                StartUnix + offset, TemplateId: id, TemplateName: name, TemplateLanguage: "es")));

    private async Task ProcessAsync(params string[] bodies)
    {
        await using var db = database.CreateContext();
        foreach (var body in bodies)
        {
            db.WebhookInbox.Add(new WebhookInbox { ReceivedUtc = _clock.UtcNow, Body = body });
        }

        await db.SaveChangesAsync();
        await TestServices.Processor(db, _clock).ProcessBatchAsync(CancellationToken.None);
    }
}
