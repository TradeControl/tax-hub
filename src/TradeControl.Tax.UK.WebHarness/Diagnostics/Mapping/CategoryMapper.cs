namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Mapping;

public sealed class CategoryMapper
{
    public decimal ClampNonNegative(decimal value)
    {
        return value < 0 ? 0 : value;
    }

    public int ToWholeNumber(decimal value)
    {
        var clamped = ClampNonNegative(value);
        return (int)decimal.Round(clamped, 0, MidpointRounding.AwayFromZero);
    }

    public decimal ToAmount(decimal value)
    {
        var clamped = ClampNonNegative(value);
        return decimal.Round(clamped, 2, MidpointRounding.AwayFromZero);
    }
}
