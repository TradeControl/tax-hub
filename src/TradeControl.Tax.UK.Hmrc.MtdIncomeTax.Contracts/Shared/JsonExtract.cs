using System.Text.Json;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.Shared;

public static class JsonExtract
{
    public static string? GetString(JsonElement json, string name)
    {
        return json.TryGetProperty(name, out var el) ? el.GetString() : null;
    }

    public static decimal GetDecimal(JsonElement json, string name)
    {
        return json.TryGetProperty(name, out var el) && el.TryGetDecimal(out var v)
            ? v
            : 0m;
    }

    public static decimal? GetDecimalNullable(JsonElement json, string name)
    {
        return json.TryGetProperty(name, out var el) && el.TryGetDecimal(out var v)
            ? v
            : null;
    }

    public static DateOnly GetDateOnly(JsonElement json, string name)
    {
        if (json.TryGetProperty(name, out var el))
        {
            var s = el.GetString();
            if (!string.IsNullOrEmpty(s) && DateOnly.TryParse(s, out var d))
                return d;
        }
        return default;
    }

    public static DateOnly? GetDateOnlyNullable(JsonElement json, string name)
    {
        if (json.TryGetProperty(name, out var el))
        {
            var s = el.GetString();
            if (!string.IsNullOrEmpty(s) && DateOnly.TryParse(s, out var d))
                return d;
        }
        return null;
    }
}

