using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Consent;
using WaBulk.Infrastructure.Contacts;
using WaBulk.Tests.Meta;
using WaBulk.Tests.Support;

namespace WaBulk.Tests.Contacts;

/// <summary>
/// F1.4 contra SQL Server: mensajes entrantes y contactos (BSUID ↔ teléfono), oferta de promociones (D-031) y su
/// respuesta, baja manual, importación y listas. Payloads con la forma de la referencia de Meta
/// (webhooks/reference/messages/interactive y messages/text).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContactConsentTests(SqlServerTestDatabase database)
    : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private const string Phone = "+59171234567";
    private const string WaId = "59171234567";
    private const string Bsuid = "BO.1880967352889250";
    private static readonly DateTime Start = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long StartUnix = new DateTimeOffset(Start).ToUnixTimeSeconds();

    private readonly ManualClock _clock = new(Start);
    private int _wamids;

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Mensajes entrantes y contactos ----------

    [Fact]
    public async Task Hola_GuardaElMensaje_YCreaElContactoConTelefonoBsuidYNombre()
    {
        await ProcessAsync(Text("Hola", "wamid.HOLA"));

        await using var db = database.CreateContext();
        var contact = await db.Contacts.SingleAsync();
        Assert.Equal((Phone, WaId, Bsuid, "Andrés"), (contact.PhoneE164, contact.WaId, contact.UserId, contact.Name));
        var message = await db.InboundMessages.SingleAsync();
        Assert.Equal((contact.Id, "text", "Hola", Start.AddSeconds(10)), (message.ContactId, message.Type, message.Text, message.SentUtc));
        Assert.Equal(WebhookInboxStatus.Procesado, (await db.WebhookInbox.SingleAsync()).Status);
        Assert.Empty(await db.ConsentRecords.ToListAsync()); // Un "Hola" no cambia el consentimiento.
    }

    [Fact]
    public async Task MismoWebhookDosVeces_UnSoloMensaje()
    {
        await ProcessAsync(Text("Hola", "wamid.REPETIDO"), Text("Hola", "wamid.REPETIDO"));

        await using var db = database.CreateContext();
        Assert.Single(await db.InboundMessages.ToListAsync());
        Assert.Single(await db.Contacts.ToListAsync());
    }

    [Fact]
    public async Task BajaSoloConBsuid_SeAplicaAlTelefonoMapeado()
    {
        // Primero escribe con teléfono (queda el mapeo); después adopta un nombre de usuario y Meta omite el número.
        await ProcessAsync(Text("Hola", "wamid.CONTEL"));
        await ProcessAsync(Text("BAJA", "wamid.SINTEL", offset: 60, withPhone: false));

        await using var db = database.CreateContext();
        var record = await db.ConsentRecords.SingleAsync();
        Assert.Equal((Phone, ConsentStatus.OptOut, ConsentSource.PalabraClave), (record.PhoneE164, record.Status, record.Source));
        Assert.Equal(await db.Contacts.Select(c => c.Id).SingleAsync(), record.ContactId);
        Assert.All(await db.WebhookInbox.ToListAsync(), r => Assert.Equal(WebhookInboxStatus.Procesado, r.Status));
    }

    // ---------- Oferta de promociones (D-031) ----------

    [Fact]
    public async Task Ofrecer_GuardaYAuditaAntesDelPost_EnviaBotonesConCallback()
    {
        var contactId = await ClientWroteAsync();
        var handler = Accepting();

        var result = await OfferAsync(contactId, handler);

        Assert.True(result.Sent);
        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("interactive", root.GetProperty("type").GetString());
        Assert.Equal(WaId, root.GetProperty("to").GetString());
        Assert.Equal($"oferta:{result.Offer!.Id}", root.GetProperty("biz_opaque_callback_data").GetString());
        var interactive = root.GetProperty("interactive");
        Assert.Equal("button", interactive.GetProperty("type").GetString());
        Assert.Contains("como máximo 2 promociones por semana", interactive.GetProperty("body").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            [(OfferButtons.Si, "Sí, quiero ofertas"), (OfferButtons.AhoraNo, "Ahora no")],
            interactive.GetProperty("action").GetProperty("buttons").EnumerateArray()
                .Select(b => (b.GetProperty("reply").GetProperty("id").GetString(), b.GetProperty("reply").GetProperty("title").GetString())));

        await using var db = database.CreateContext();
        var offer = await db.ConsentOffers.SingleAsync();
        Assert.Equal((ConsentOfferStatus.Enviada, "wamid.OFERTA1", "v1", "andres"), (offer.Status, offer.Wamid, offer.TextVersion, offer.OfferedBy));
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.OfrecerPromociones);
        Assert.Equal(("andres", "Contacto", contactId.ToString(CultureInfo.InvariantCulture)), (audit.User, audit.Entity, audit.EntityId));
    }

    [Fact]
    public async Task OfertaSinVentanaAbierta_SeRechazaSinPost()
    {
        var contactId = await ClientWroteAsync();
        _clock.Advance(TimeSpan.FromHours(24)); // El último mensaje fue hace 24 h: ventana cerrada.
        var handler = Accepting();

        var result = await OfferAsync(contactId, handler);

        Assert.Equal((false, OfferRefusals.SinVentana), (result.Sent, result.Refusal));
        Assert.Empty(handler.Requests);
        await using var db = database.CreateContext();
        Assert.Empty(await db.ConsentOffers.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Si_RegistraOptInConWamidOrigenYEvidencia_YRepetirElWebhookNoDuplica()
    {
        var contactId = await ClientWroteAsync();
        await OfferAsync(contactId, Accepting());

        var reply = Reply(OfferButtons.Si, "Sí, quiero ofertas", "wamid.SI", context: "wamid.OFERTA1");
        await ProcessAsync(reply);
        await ProcessAsync(reply); // Meta reintenta el mismo webhook.

        await using var db = database.CreateContext();
        var record = await db.ConsentRecords.SingleAsync();
        Assert.Equal((ConsentStatus.OptIn, ConsentSource.Boton, "wamid.SI", contactId), (record.Status, record.Source, record.SourceWamid, record.ContactId));
        Assert.Contains("id=OPTIN_PROMOS", record.Evidence, StringComparison.Ordinal);
        Assert.Contains("texto v1 ofrecida por andres", record.Evidence, StringComparison.Ordinal);
        Assert.Contains("wamid oferta wamid.OFERTA1", record.Evidence, StringComparison.Ordinal);
        var offer = await db.ConsentOffers.SingleAsync();
        Assert.Equal((ConsentOfferResponse.Si, "wamid.SI"), (offer.Response, offer.ResponseWamid));
        Assert.True((await new ConsentService(db, _clock).EvaluateAsync(Phone, CancellationToken.None)).Eligible);
    }

    [Fact]
    public async Task AhoraNo_NoDaOptIn_YNoSeReofreceDentroDelPlazo()
    {
        var contactId = await ClientWroteAsync();
        await OfferAsync(contactId, Accepting());
        await ProcessAsync(Reply(OfferButtons.AhoraNo, "Ahora no", "wamid.NO", context: "wamid.OFERTA1"));

        await using (var db = database.CreateContext())
        {
            Assert.Empty(await db.ConsentRecords.ToListAsync());
            Assert.Equal(ConsentOfferResponse.AhoraNo, (await db.ConsentOffers.SingleAsync()).Response);
        }

        // Al día siguiente vuelve a escribir (ventana abierta), pero aún dentro de los 30 días: no se ofrece.
        _clock.Advance(TimeSpan.FromDays(1));
        await ProcessAsync(Text("Otra consulta", "wamid.CONSULTA2", offset: 86_400 + 60));
        var handler = Accepting();
        Assert.Equal(OfferRefusals.RechazoReciente, (await OfferAsync(contactId, handler)).Refusal);
        Assert.Empty(handler.Requests);

        // Pasado el plazo, con la ventana abierta otra vez, sí.
        _clock.Advance(TimeSpan.FromDays(30));
        await ProcessAsync(Text("Hola de nuevo", "wamid.CONSULTA3", offset: (31 * 86_400) + 60));
        Assert.True((await OfferAsync(contactId, Accepting())).Sent);
    }

    [Fact]
    public async Task SiDespuesDeUnaBaja_QuedaVigenteElOptIn()
    {
        await ProcessAsync(Text("BAJA", "wamid.BAJA"));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var contactId = await ClientWroteAsync(offset: 300); // Vuelve a escribir: ventana abierta.
        Assert.True((await OfferAsync(contactId, Accepting())).Sent);

        await ProcessAsync(Reply(OfferButtons.Si, "Sí, quiero ofertas", "wamid.SI2", context: "wamid.OFERTA1", offset: 400));

        await using var db = database.CreateContext();
        Assert.Equal(ConsentStatus.OptIn, await new ConsentService(db, _clock).CurrentCompanyStatusAsync(Phone, CancellationToken.None));
        Assert.Equal(2, await db.ConsentRecords.CountAsync()); // El historial conserva la baja.
    }

    [Fact]
    public async Task DobleClic_LaSegundaOfertaSeRechaza()
    {
        var contactId = await ClientWroteAsync();
        Assert.True((await OfferAsync(contactId, Accepting())).Sent);

        var handler = Accepting();
        Assert.Equal(OfferRefusals.OfertaPendiente, (await OfferAsync(contactId, handler)).Refusal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ConOptInVigente_NoSeOfrece()
    {
        var contactId = await ClientWroteAsync();
        await using (var db = database.CreateContext())
        {
            db.ConsentRecords.Add(new ConsentRecord
            {
                PhoneE164 = Phone, Status = ConsentStatus.OptIn, Source = ConsentSource.Manual, RecordedUtc = Start, RecordedBy = "prueba",
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(OfferRefusals.YaTieneOptIn, (await OfferAsync(contactId, Accepting())).Refusal);
    }

    [Fact]
    public async Task EstadosDeLaOferta_NoQuedanSinCorrelacion_YResuelvenUnaOfertaIncierta()
    {
        var contactId = await ClientWroteAsync();
        var timeout = new FakeHttpHandler((_, _) => throw new TaskCanceledException("timeout"));
        var result = await OfferAsync(contactId, timeout);
        Assert.Equal(ConsentOfferStatus.Incierta, result.Offer!.Status);

        await ProcessAsync(OfferStatus("sent", $"oferta:{result.Offer.Id}"));

        await using var db = database.CreateContext();
        var offer = await db.ConsentOffers.SingleAsync();
        Assert.Equal((ConsentOfferStatus.Enviada, "wamid.OFERTA-WEBHOOK"), (offer.Status, offer.Wamid));
        Assert.All(await db.WebhookInbox.ToListAsync(), r => Assert.Equal(WebhookInboxStatus.Procesado, r.Status));
    }

    [Fact]
    public async Task MensajesRecientes_UnaFilaPorContacto_ConSuUltimoMensajeYSiSePuedeOfrecer()
    {
        var contactId = await ClientWroteAsync();
        await ProcessAsync(Text("¿Tienen envío?", "wamid.ULTIMO", offset: 20));
        await ProcessAsync(Message(new()
        {
            ["id"] = "wamid.OTRO", ["timestamp"] = Unix(15), ["type"] = "text", ["text"] = new { body = "Buenas" },
        }, withPhone: true).Replace(WaId, "59176666666", StringComparison.Ordinal).Replace(Bsuid, "BO.2", StringComparison.Ordinal));
        await OfferAsync(contactId, Accepting());

        await using var db = database.CreateContext();
        var recent = await new ConsentOfferService(db, new ConsentService(db, _clock),
            MetaWhatsAppClientTests.CreateClient(Accepting()), Options.Create(new PromotionOfferOptions()), _clock)
            .RecentAsync(7, CancellationToken.None);

        Assert.Equal(["¿Tienen envío?", "Buenas"], recent.Select(r => r.LastText));
        Assert.Equal((OfferRefusals.OfertaPendiente, "Enviada"), (recent[0].Refusal, recent[0].LastOfferStatus));
        Assert.Null(recent[1].Refusal); // El otro contacto sí se puede ofrecer.
        Assert.Equal(Start.AddSeconds(20) + TimeSpan.FromHours(24) - TimeSpan.FromMinutes(10), recent[0].WindowOpenUntilUtc);
    }

    // ---------- Baja manual ----------

    [Fact]
    public async Task BajaManual_RegistraOptOutIntranetConAuditoria()
    {
        var contactId = await ClientWroteAsync();
        await using var db = database.CreateContext();
        var service = new ContactService(db, _clock);

        Assert.Equal("SinMotivo", (await service.RecordOptOutAsync("ana", contactId, " ", CancellationToken.None)).Error);
        Assert.True((await service.RecordOptOutAsync("ana", contactId, "Llamó a la tienda", CancellationToken.None)).Succeeded);

        var record = await db.ConsentRecords.SingleAsync();
        Assert.Equal((ConsentStatus.OptOut, ConsentSource.Intranet, "Llamó a la tienda", "ana"), (record.Status, record.Source, record.Evidence, record.RecordedBy));
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(AuditActions.BajaManual, audit.Action);
        Assert.Contains("SinConsentimiento", audit.Before, StringComparison.Ordinal);
        Assert.Contains("Llamó a la tienda", audit.After, StringComparison.Ordinal);
    }

    // ---------- Importación ----------

    [Fact]
    public async Task Importacion_100FilasCon5Invalidas_95Validas5Errores()
    {
        var table = new List<string[]> { new[] { "Teléfono", "Nombre" } };
        for (var i = 0; i < 100; i++)
        {
            table.Add(i % 20 == 7 ? ["123", $"Malo {i}"] : [(70_000_000 + i).ToString(CultureInfo.InvariantCulture), $"Cliente {i}"]);
        }

        await using var db = database.CreateContext();
        var report = await new ContactImportService(db, _clock).ImportAsync("ana", "clientes.csv", table, null, CancellationToken.None);

        Assert.Equal((100, 95, 5, 95), (report.Rows, report.Valid, report.Errors, report.Created));
        Assert.Equal(95, await db.Contacts.CountAsync());
        Assert.All(report.Detail.Where(d => d.Result == "Error"), d => Assert.Equal(0, (d.Row - 2) % 20 - 7));
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(AuditActions.ImportarContactos, audit.Action);
        Assert.Contains("\"validas\":95", audit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Importacion_OptInSobreUnOptOut_SigueOptOut()
    {
        await ProcessAsync(Text("BAJA", "wamid.BAJAIMP"));
        await using var db = database.CreateContext();
        List<string[]> table =
        [
            ["Telefono", "Consentimiento", "Evidencia"],
            [Phone, "OptIn", "Formulario firmado"],
            ["+59176543210", "OptIn", "Formulario firmado"],
            ["+59176543211", "OptIn", ""],
        ];

        var report = await new ContactImportService(db, _clock).ImportAsync("ana", "a.csv", table, null, CancellationToken.None);

        Assert.Equal(ConsentStatus.OptOut, await new ConsentService(db, _clock).CurrentCompanyStatusAsync(Phone, CancellationToken.None));
        Assert.Equal(["Advertencia", "Creado", "Error"], report.Detail.Select(d => d.Result));
        Assert.Contains("nunca levanta una baja", report.Detail[0].Message, StringComparison.Ordinal);
        Assert.Contains("exige Evidencia", report.Detail[2].Message, StringComparison.Ordinal);
        var optIn = await db.ConsentRecords.SingleAsync(r => r.PhoneE164 == "+59176543210");
        Assert.Equal((ConsentStatus.OptIn, ConsentSource.Importacion), (optIn.Status, optIn.Source));
        Assert.NotNull(optIn.ContactId);
    }

    [Fact]
    public async Task Importacion_ConLista_AgregaLosContactos()
    {
        await using var db = database.CreateContext();
        var segments = new SegmentService(db, _clock);
        var (_, segment) = await segments.CreateAsync("ana", "Clientes VIP", null, CancellationToken.None);
        List<string[]> table = [["Celular"], ["71111111"], ["72222222"]];

        await new ContactImportService(db, _clock).ImportAsync("ana", "vip.xlsx", table, segment!.Id, CancellationToken.None);

        Assert.Equal(2, (await segments.ListAsync(CancellationToken.None)).Single().Members);
        var contactId = await db.Contacts.Where(c => c.PhoneE164 == "+59171111111").Select(c => c.Id).SingleAsync();
        Assert.True((await segments.RemoveMemberAsync("ana", segment.Id, contactId, CancellationToken.None)).Succeeded);
        Assert.Equal(
            [AuditActions.CrearLista, AuditActions.ImportarContactos, AuditActions.QuitarDeLista],
            await db.AuditLogs.OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
    }

    // ---------- Ayudantes ----------

    private static string Envelope(object value) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[] { new { id = "2088365445132575", changes = new[] { new { field = "messages", value } } } },
    });

    private static readonly object Metadata = new { display_phone_number = "15551892694", phone_number_id = "1524289747428097" };

    private static string Message(Dictionary<string, object> message, bool withPhone)
    {
        var contact = new Dictionary<string, object> { ["profile"] = new { name = "Andrés" }, ["user_id"] = Bsuid };
        message["from_user_id"] = Bsuid;
        if (withPhone)
        {
            contact["wa_id"] = WaId;
            message["from"] = WaId;
        }

        return Envelope(new { messaging_product = "whatsapp", metadata = Metadata, contacts = new[] { contact }, messages = new[] { message } });
    }

    private static string Unix(long offset) => (StartUnix + offset).ToString(CultureInfo.InvariantCulture);

    private static string Text(string body, string id, long offset = 10, bool withPhone = true) => Message(new()
    {
        ["id"] = id, ["timestamp"] = Unix(offset), ["type"] = "text", ["text"] = new { body },
    }, withPhone);

    private static string Reply(string buttonId, string title, string id, string context, long offset = 30) => Message(new()
    {
        ["context"] = new { from = "15551892694", id = context },
        ["id"] = id,
        ["timestamp"] = Unix(offset),
        ["type"] = "interactive",
        ["interactive"] = new { type = "button_reply", button_reply = new { id = buttonId, title } },
    }, withPhone: true);

    private static string OfferStatus(string status, string callback) => Envelope(new
    {
        messaging_product = "whatsapp",
        metadata = Metadata,
        statuses = new[]
        {
            new { id = "wamid.OFERTA-WEBHOOK", status, timestamp = Unix(20), recipient_id = WaId, biz_opaque_callback_data = callback },
        },
    });

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

    /// <summary>El cliente escribe "Hola" (abre la ventana de 24 h). Devuelve el Id de su contacto.</summary>
    private async Task<long> ClientWroteAsync(long offset = 10)
    {
        await ProcessAsync(Text("Hola", $"wamid.IN{Interlocked.Increment(ref _wamids)}", offset));
        await using var db = database.CreateContext();
        return await db.Contacts.Where(c => c.PhoneE164 == Phone).Select(c => c.Id).SingleAsync();
    }

    private int _offers;

    private FakeHttpHandler Accepting() => new((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
        $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.OFERTA{{Interlocked.Increment(ref _offers)}}"}]}""")));

    private async Task<OfferResult> OfferAsync(long contactId, FakeHttpHandler handler)
    {
        await using var db = database.CreateContext();
        var service = new ConsentOfferService(db, new ConsentService(db, _clock), MetaWhatsAppClientTests.CreateClient(handler),
            Options.Create(new PromotionOfferOptions()), _clock);
        return await service.OfferAsync("andres", contactId, CancellationToken.None);
    }
}
