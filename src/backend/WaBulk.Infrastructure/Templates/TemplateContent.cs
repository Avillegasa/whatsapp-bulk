using System.Text.Json;
using System.Text.Json.Nodes;
using WaBulk.Domain.Templates;

namespace WaBulk.Infrastructure.Templates;

/// <summary>
/// Contenido legible de una plantilla a partir de components (JSON de Meta o el que se envió al crearla). Meta devuelve
/// los tipos en mayúsculas (BODY, QUICK_REPLY) y acepta minúsculas al crear: se comparan sin distinguir.
/// </summary>
public sealed record TemplateContent(
    string? Header, string? Body, string? Footer, IReadOnlyList<string> Buttons, IReadOnlyList<string> BodyParameters)
{
    public static TemplateContent Parse(string? componentsJson)
    {
        string? header = null, body = null, footer = null;
        var buttons = new List<string>();
        if (!string.IsNullOrWhiteSpace(componentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(componentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in doc.RootElement.EnumerateArray())
                    {
                        var type = Text(c, "type")?.ToUpperInvariant();
                        switch (type)
                        {
                            case "HEADER" when Text(c, "format")?.ToUpperInvariant() is null or "TEXT":
                                header = Text(c, "text");
                                break;
                            case "HEADER":
                                header = $"[{Text(c, "format")}]";
                                break;
                            case "BODY":
                                body = Text(c, "text");
                                break;
                            case "FOOTER":
                                footer = Text(c, "text");
                                break;
                            case "BUTTONS" when c.TryGetProperty("buttons", out var list) && list.ValueKind == JsonValueKind.Array:
                                buttons.AddRange(list.EnumerateArray().Select(b => Text(b, "text")).OfType<string>());
                                break;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Componentes ilegibles: se muestra la plantilla sin contenido; la sincronización los vuelve a traer.
            }
        }

        return new TemplateContent(header, body, footer, buttons, TemplateRules.Parameters(body));
    }

    /// <summary>
    /// Cuerpo de POST /{WABA_ID}/message_templates para una plantilla de marketing con parámetros con nombre y los dos
    /// botones de respuesta rápida de D-012 (misma forma que tools/plantillas/promo_demo.json, aceptada por Meta en F1.1).
    /// https://developers.facebook.com/documentation/business-messaging/whatsapp/templates/marketing-templates/custom-marketing-templates
    /// </summary>
    public static JsonObject BuildCreateRequest(TemplateDraft draft, string interestButton, string optOutButton)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var components = new JsonArray();
        if (!string.IsNullOrWhiteSpace(draft.HeaderText))
        {
            components.Add(new JsonObject { ["type"] = "header", ["format"] = "text", ["text"] = draft.HeaderText.Trim() });
        }

        var bodyText = draft.BodyText!.Trim();
        var body = new JsonObject { ["type"] = "body", ["text"] = bodyText };
        var parameters = TemplateRules.Parameters(bodyText);
        if (parameters.Count > 0)
        {
            var examples = new JsonArray();
            foreach (var p in parameters)
            {
                examples.Add(new JsonObject { ["param_name"] = p, ["example"] = draft.Examples![p].Trim() });
            }

            body["example"] = new JsonObject { ["body_text_named_params"] = examples };
        }

        components.Add(body);
        if (!string.IsNullOrWhiteSpace(draft.FooterText))
        {
            components.Add(new JsonObject { ["type"] = "footer", ["text"] = draft.FooterText.Trim() });
        }

        components.Add(new JsonObject
        {
            ["type"] = "buttons",
            ["buttons"] = new JsonArray(
                new JsonObject { ["type"] = "quick_reply", ["text"] = interestButton },
                new JsonObject { ["type"] = "quick_reply", ["text"] = optOutButton }),
        });

        return new JsonObject
        {
            ["name"] = draft.Name!.Trim(),
            ["language"] = draft.Language!.Trim(),
            ["category"] = TemplateCategories.Marketing,
            ["parameter_format"] = "named",
            ["components"] = components,
        };
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
