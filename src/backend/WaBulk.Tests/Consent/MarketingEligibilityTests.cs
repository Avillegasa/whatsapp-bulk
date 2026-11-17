using WaBulk.Domain.Consent;

namespace WaBulk.Tests.Consent;

/// <summary>Reglas de bajas (F0.10, regla 8): dos capas independientes y detección en mensajes entrantes.</summary>
public sealed class MarketingEligibilityTests
{
    private static readonly string[] Keywords = ["BAJA", "STOP", "SALIR", "DETENER", "CANCELAR"];

    [Theory]
    [InlineData(null, null, false, SkipReasons.SinConsentimiento)]
    [InlineData(ConsentStatus.OptIn, null, true, null)]
    [InlineData(ConsentStatus.OptIn, PlatformPreferenceValue.Resume, true, null)]
    [InlineData(ConsentStatus.OptIn, PlatformPreferenceValue.Stop, false, SkipReasons.BajaPlataforma)]
    [InlineData(ConsentStatus.OptOut, null, false, SkipReasons.BajaEmpresa)]
    [InlineData(ConsentStatus.OptOut, PlatformPreferenceValue.Resume, false, SkipReasons.BajaEmpresa)] // resume no levanta la baja de la empresa
    [InlineData(ConsentStatus.NoContactar, PlatformPreferenceValue.Resume, false, SkipReasons.NoContactar)]
    public void Elegibilidad_por_capas(ConsentStatus? company, PlatformPreferenceValue? platform, bool eligible, string? reason)
    {
        var result = MarketingEligibility.Evaluate(company, platform);

        Assert.Equal(eligible, result.Eligible);
        Assert.Equal(reason, result.SkipReason);
    }

    [Theory]
    [InlineData(null, ConsentSource.Importacion, true)]
    [InlineData(ConsentStatus.OptIn, ConsentSource.Importacion, true)]
    [InlineData(ConsentStatus.OptOut, ConsentSource.Importacion, false)]
    [InlineData(ConsentStatus.NoContactar, ConsentSource.Importacion, false)]
    [InlineData(ConsentStatus.OptOut, ConsentSource.Manual, true)]
    [InlineData(ConsentStatus.OptOut, ConsentSource.Intranet, true)]
    public void Una_importacion_nunca_levanta_una_baja(ConsentStatus? current, ConsentSource source, bool allowed) =>
        Assert.Equal(allowed, MarketingEligibility.CanRecordOptIn(current, source));

    [Theory]
    [InlineData("BAJA")]
    [InlineData("baja")]
    [InlineData("  Baja.  ")]
    [InlineData("¡BAJA!")]
    [InlineData("Bája")]
    [InlineData("stop")]
    [InlineData("Salir")]
    [InlineData("cancelar\n")]
    public void Palabra_clave_reconocida(string text) => Assert.True(OptOutDetector.IsKeyword(text, Keywords));

    [Theory]
    [InlineData("no quiero salir de aquí")]
    [InlineData("bajas de precio?")]
    [InlineData("hola")]
    [InlineData("")]
    [InlineData(null)]
    public void Texto_que_no_es_baja(string? text) => Assert.False(OptOutDetector.IsKeyword(text, Keywords));

    [Theory]
    [InlineData("BAJA_PROMOCIONES", "otro texto", true)]
    [InlineData(null, "No recibir más", true)]
    [InlineData(null, "no recibir mas", true)]
    [InlineData("ME_INTERESA", "Me interesa", false)]
    [InlineData(null, null, false)]
    public void Boton_de_baja(string? payload, string? text, bool expected) =>
        Assert.Equal(expected, OptOutDetector.IsOptOutButton(payload, text, ["BAJA_PROMOCIONES"], ["No recibir más"]));
}
