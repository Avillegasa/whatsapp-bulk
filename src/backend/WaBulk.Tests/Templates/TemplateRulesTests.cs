using System.Net;
using System.Text.Json;
using WaBulk.Domain.Templates;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Templates;
using WaBulk.Tests.Meta;

namespace WaBulk.Tests.Templates;

/// <summary>
/// Plantillas (F1.5) sin base de datos: reglas de creación, cuerpo del POST de creación, lectura de components, envío
/// con parámetros y cliente de la API de plantillas con HTTP falso.
/// </summary>
public sealed class TemplateRulesTests
{
    private const string Interest = "Me interesa";
    private const string OptOut = "No recibir más";

    internal static TemplateDraft ValidDraft(string name = "promo_diciembre") => new(
        name, "es", "Ofertas de la semana", "Hola {{nombre}}, esta semana: {{promocion}}. ¿Te interesa?", "Responde BAJA para no recibir más",
        new Dictionary<string, string> { ["nombre"] = "Andrés", ["promocion"] = "15% en cortes" });

    [Fact]
    public void Plantilla_valida_sin_errores()
    {
        Assert.Empty(TemplateRules.Validate(ValidDraft(), Interest, OptOut));
    }

    [Theory]
    [InlineData("Promo Diciembre", "nombre")]
    [InlineData("promo-diciembre", "nombre")]
    [InlineData("", "nombre")]
    public void Nombre_invalido(string name, string expected)
    {
        var errors = TemplateRules.Validate(ValidDraft() with { Name = name }, Interest, OptOut);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Falta_ejemplo_de_un_parametro()
    {
        var draft = ValidDraft() with { Examples = new Dictionary<string, string> { ["nombre"] = "Ana" } };
        var error = Assert.Single(TemplateRules.Validate(draft, Interest, OptOut));
        Assert.Contains("{{promocion}}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Limites_de_Meta_y_parametros_en_encabezado()
    {
        var draft = ValidDraft() with
        {
            BodyText = new string('a', 1025),
            HeaderText = "Hola {{nombre}}",
            FooterText = new string('b', 61),
            Language = "español",
        };
        var errors = TemplateRules.Validate(draft, Interest, OptOut);
        Assert.Contains(errors, e => e.Contains("1024", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("encabezado no admite parámetros", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("pie admite hasta 60", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Idioma", StringComparison.Ordinal));
    }

    [Fact]
    public void Parametro_posicional_o_con_mayusculas_se_rechaza_al_crear()
    {
        var draft = ValidDraft() with
        {
            BodyText = "Hola {{1}} y {{Nombre}}",
            Examples = new Dictionary<string, string> { ["1"] = "a", ["Nombre"] = "b" },
        };
        Assert.Equal(2, TemplateRules.Validate(draft, Interest, OptOut).Count(e => e.Contains("inválido", StringComparison.Ordinal)));
    }

    [Fact]
    public void Boton_configurado_demasiado_largo()
    {
        Assert.Contains(TemplateRules.Validate(ValidDraft(), new string('x', 26), OptOut),
            e => e.Contains("revisar la configuración", StringComparison.Ordinal));
    }

    [Fact]
    public void Parametros_en_orden_de_aparicion_sin_repetir()
    {
        Assert.Equal(["nombre", "promocion"], TemplateRules.Parameters("{{nombre}} {{promocion}} y otra vez {{nombre}}"));
        Assert.True(TemplateRules.IsPositional(TemplateRules.Parameters("Hola {{1}}, tu código {{2}}")));
        Assert.Equal("en_US", TemplateRules.NormalizeLanguage("en-US"));
    }

    [Fact]
    public void Cuerpo_de_creacion_marketing_named_con_los_dos_botones()
    {
        var body = TemplateContent.BuildCreateRequest(ValidDraft(), Interest, OptOut);

        using var json = JsonDocument.Parse(body.ToJsonString());
        var root = json.RootElement;
        Assert.Equal("promo_diciembre", root.GetProperty("name").GetString());
        Assert.Equal("es", root.GetProperty("language").GetString());
        Assert.Equal("MARKETING", root.GetProperty("category").GetString());
        Assert.Equal("named", root.GetProperty("parameter_format").GetString());
        var components = root.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(["header", "body", "footer", "buttons"], components.Select(c => c.GetProperty("type").GetString()));
        var examples = components[1].GetProperty("example").GetProperty("body_text_named_params").EnumerateArray()
            .Select(e => (e.GetProperty("param_name").GetString(), e.GetProperty("example").GetString())).ToList();
        Assert.Equal([("nombre", "Andrés"), ("promocion", "15% en cortes")], examples);
        var buttons = components[3].GetProperty("buttons").EnumerateArray()
            .Select(b => (b.GetProperty("type").GetString(), b.GetProperty("text").GetString())).ToList();
        Assert.Equal([("quick_reply", Interest), ("quick_reply", OptOut)], buttons);
    }

    [Fact]
    public void Contenido_desde_components_de_Meta_en_mayusculas()
    {
        const string components = """
            [{"type":"HEADER","format":"TEXT","text":"Ofertas"},
             {"type":"BODY","text":"Hola {{nombre}}: {{promocion}}","example":{"body_text_named_params":[]}},
             {"type":"FOOTER","text":"Pie"},
             {"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Me interesa"},{"type":"QUICK_REPLY","text":"No recibir más"}]}]
            """;
        var content = TemplateContent.Parse(components);
        Assert.Equal(("Ofertas", "Pie"), (content.Header, content.Footer));
        Assert.Equal(["nombre", "promocion"], content.BodyParameters);
        Assert.Equal(["Me interesa", "No recibir más"], content.Buttons);
        Assert.Empty(TemplateContent.Parse("no es json").Buttons);
    }

    // ---------- Envío con components ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Envio_lleva_los_parametros_del_cuerpo(bool named)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(
            MetaWhatsAppClientTests.Json(HttpStatusCode.OK, MetaWhatsAppClientTests.AcceptedJson)));
        var message = new TemplateMessage("promo_demo", "es",
            [new TemplateParameter("nombre", "Ana"), new TemplateParameter("promocion", "2x1")], named);

        await MetaWhatsAppClientTests.CreateClient(handler).SendTemplateAsync("+59170000000", message, Guid.NewGuid(), CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var component = Assert.Single(json.RootElement.GetProperty("template").GetProperty("components").EnumerateArray());
        Assert.Equal("body", component.GetProperty("type").GetString());
        var parameters = component.GetProperty("parameters").EnumerateArray().ToList();
        Assert.Equal(["Ana", "2x1"], parameters.Select(p => p.GetProperty("text").GetString()));
        Assert.All(parameters, p => Assert.Equal("text", p.GetProperty("type").GetString()));
        Assert.Equal(named, parameters[0].TryGetProperty("parameter_name", out var n) && n.GetString() == "nombre");
    }

    [Fact]
    public async Task Envio_sin_parametros_no_lleva_components()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(
            MetaWhatsAppClientTests.Json(HttpStatusCode.OK, MetaWhatsAppClientTests.AcceptedJson)));

        await MetaWhatsAppClientTests.CreateClient(handler)
            .SendTemplateAsync("+59170000000", new TemplateMessage("hello_world", "en_US"), Guid.NewGuid(), CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.False(json.RootElement.GetProperty("template").TryGetProperty("components", out _));
    }

    // ---------- API de plantillas ----------

    internal static MetaTemplateApiClient TemplateClient(FakeHttpHandler handler)
    {
        var options = MetaWhatsAppClientTests.TestOptions();
        options.WabaId = "WABA1";
        return new MetaTemplateApiClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) },
            new MetaWhatsAppClientTests.StaticOptionsMonitor(options));
    }

    [Fact]
    public async Task Listar_recorre_las_paginas_con_el_cursor_after()
    {
        var handler = new FakeHttpHandler((request, _) =>
        {
            var second = request.RequestUri!.Query.Contains("after=CUR1", StringComparison.Ordinal);
            var json = second
                ? """{"data":[{"id":"2","name":"hello_world","language":"en_US","status":"APPROVED","category":"UTILITY"}],"paging":{"cursors":{"before":"B","after":"CUR2"}}}"""
                : """{"data":[{"id":"1","name":"promo_demo","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"NAMED","quality_score":{"score":"GREEN","date":1763380800},"components":[{"type":"BODY","text":"Hola {{nombre}}"}]}],"paging":{"cursors":{"before":"B","after":"CUR1"},"next":"https://graph.test/next"}}""";
            return Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK, json));
        });

        var list = await TemplateClient(handler).ListAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("https://graph.test/v99.0/WABA1/message_templates?fields=", handler.Requests[0].Request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal(["promo_demo", "hello_world"], list.Select(t => t.Name));
        Assert.Equal(("GREEN", "NAMED"), (list[0].QualityScore, list[0].ParameterFormat));
        Assert.Contains("{{nombre}}", list[0].ComponentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crear_clasifica_respuesta_rechazo_y_sin_respuesta()
    {
        var created = await TemplateClient(new FakeHttpHandler((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.OK,
            """{"id":"1627019861106475","status":"PENDING","category":"MARKETING"}""")))).CreateAsync(new(), CancellationToken.None);
        Assert.Equal((MetaCreateOutcome.Created, "1627019861106475", "PENDING"), (created.Outcome, created.Id, created.Status));

        var rejected = await TemplateClient(new FakeHttpHandler((_, _) => Task.FromResult(MetaWhatsAppClientTests.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"Invalid parameter","type":"OAuthException","code":100,"error_subcode":2388024,"error_user_title":"Content in This Language Already Exists","error_user_msg":"Ya existe","fbtrace_id":"X"}}""")))).CreateAsync(new(), CancellationToken.None);
        Assert.Equal((MetaCreateOutcome.Rejected, 100, 2388024), (rejected.Outcome, rejected.ErrorCode, rejected.ErrorSubcode));
        Assert.Contains("Content in This Language Already Exists", rejected.Detail, StringComparison.Ordinal);

        var unknown = await TemplateClient(new FakeHttpHandler((_, _) => throw new TaskCanceledException("timeout")))
            .CreateAsync(new(), CancellationToken.None);
        Assert.Equal(MetaCreateOutcome.Unknown, unknown.Outcome);
    }
}
