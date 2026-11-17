using WaBulk.Domain.Capacity;

namespace WaBulk.Tests.Capacity;

public sealed class CapacityBudgetTests
{
    [Theory]
    [InlineData(250, 90, 200, 25)]      // TIER_250 inicial: 225 − 200 manuales.
    [InlineData(2000, 90, 200, 1600)]   // TIER_2K.
    [InlineData(10000, 90, 200, 8800)]
    [InlineData(250, 90, 300, 0)]       // El cupo manual supera lo usable: el sistema no envía.
    [InlineData(255, 90, 0, 229)]       // 229,5 → se redondea hacia abajo.
    public void Presupuesto_es_limite_por_porcentaje_menos_cupo_manual(int limit, int percent, int manual, int expected) =>
        Assert.Equal(expected, CapacityBudget.Compute(limit, percent, manual));
}
