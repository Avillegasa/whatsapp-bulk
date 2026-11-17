using System.Text;
using ClosedXML.Excel;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Infrastructure.Contacts;

namespace WaBulk.Tests.Contacts;

/// <summary>Reglas puras de F1.4: teléfonos E.164, ventana de 24 h, cuándo se puede ofrecer, lectura de CSV/Excel.</summary>
public sealed class ContactRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("70000000", "+59170000000")]
    [InlineData("6123 4567", "+59161234567")]
    [InlineData("591 7000-0000", "+59170000000")]
    [InlineData("+591 (7) 000.0000", "+59170000000")]
    [InlineData("+54 9 11 1234 5678", "+5491112345678")]
    public void Telefono_Valido_SeNormalizaAE164(string input, string expected)
    {
        Assert.True(PhoneNumbers.TryNormalize(input, out var e164, out var error), error);
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("22123456")] // Fijo de La Paz: WhatsApp no llega.
    [InlineData("+5917000000")] // 591 + 7 dígitos.
    [InlineData("5491112345678")] // Otro país sin "+".
    [InlineData("70000000 int 2")]
    [InlineData("+591+70000000")]
    public void Telefono_Invalido_DiceElMotivo(string input)
    {
        Assert.False(PhoneNumbers.TryNormalize(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Ventana_SeCierraALas24hMenosElMargen()
    {
        var margin = TimeSpan.FromMinutes(10);
        Assert.True(ConsentOfferRules.IsWindowOpen(Now, Now.AddHours(23).AddMinutes(49), margin));
        Assert.False(ConsentOfferRules.IsWindowOpen(Now, Now.AddHours(23).AddMinutes(50), margin));
        Assert.False(ConsentOfferRules.IsWindowOpen(null, Now, margin));
    }

    [Theory]
    [InlineData(false, true, null, null, null, OfferRefusals.ContactoInactivo)]
    [InlineData(true, true, ConsentStatus.NoContactar, null, null, OfferRefusals.NoContactar)]
    [InlineData(true, true, ConsentStatus.OptIn, null, null, OfferRefusals.YaTieneOptIn)]
    [InlineData(true, true, null, -10.0, null, OfferRefusals.RechazoReciente)]
    [InlineData(true, true, null, -31.0, null, null)]
    [InlineData(true, true, null, null, -0.5, OfferRefusals.OfertaPendiente)]
    [InlineData(true, true, null, null, -2.0, null)]
    [InlineData(true, false, null, null, null, OfferRefusals.SinVentana)]
    [InlineData(true, true, ConsentStatus.OptOut, null, null, null)] // Pidió la baja y volvió a escribir: se le puede preguntar.
    public void PuedeOfrecer(bool active, bool window, ConsentStatus? consent, double? declinedDaysAgo, double? pendingDaysAgo, string? expected)
    {
        var refusal = ConsentOfferRules.Check(active, window, consent,
            declinedDaysAgo is { } d ? Now.AddDays(d) : null, pendingDaysAgo is { } p ? Now.AddDays(p) : null,
            Now, TimeSpan.FromDays(30));
        Assert.Equal(expected, refusal);
    }

    [Fact]
    public void Opciones_FueraDeLosLimitesDeMeta_NoSonValidas()
    {
        Assert.True(new PromotionOfferOptions().IsValid(out _));
        Assert.False(new PromotionOfferOptions { YesTitle = "Sí, quiero recibir todas las ofertas" }.IsValid(out _));
        Assert.False(new PromotionOfferOptions { NotNowTitle = "Sí, quiero ofertas" }.IsValid(out _));
        Assert.False(new PromotionOfferOptions { Text = new string('x', 1025) }.IsValid(out _));
    }

    [Fact]
    public void Csv_PuntoYComa_ComillasYLatin1()
    {
        // Excel en español guarda "CSV (delimitado por comas)" con ";" y en Windows-1252.
        var csv = "Teléfono;Nombre;Evidencia\r\n70000000;\"Pérez; Juan\";\"Dijo \"\"sí\"\"\"\r\n71111111;Ana;\r\n";
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(csv));

        var rows = ContactImportService.ReadTable(stream, "clientes.csv");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["Teléfono", "Nombre", "Evidencia"], rows[0]);
        Assert.Equal(["70000000", "Pérez; Juan", "Dijo \"sí\""], rows[1]);
        Assert.Equal(["71111111", "Ana", ""], rows[2]);
    }

    [Fact]
    public void Excel_TelefonoNumerico_NoSaleEnNotacionCientifica()
    {
        using var stream = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Hoja1");
            sheet.Cell(1, 1).Value = "Telefono";
            sheet.Cell(1, 2).Value = "Nombre";
            sheet.Cell(2, 1).Value = 59170000000;
            sheet.Cell(2, 2).Value = "Ana";
            workbook.SaveAs(stream);
        }

        stream.Position = 0;
        var rows = ContactImportService.ReadTable(stream, "clientes.xlsx");

        Assert.Equal(["59170000000", "Ana"], rows[1]);
    }

    [Fact]
    public void Formato_NoAdmitido()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => ContactImportService.ReadTable(stream, "clientes.xls"));
    }
}
