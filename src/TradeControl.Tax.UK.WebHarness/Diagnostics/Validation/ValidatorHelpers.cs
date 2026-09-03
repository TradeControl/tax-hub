using System.Globalization;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

internal static class ValidatorHelpers
{
    public static void RequireKeys(
        Dictionary<string, object?> parameters,
        ValidationResult result,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!parameters.ContainsKey(key))
            {
                result.AddError($"Missing required parameter '{key}'.");
            }
        }
    }

    public static void RejectUnusedKeys(
        Dictionary<string, object?> parameters,
        ValidationResult result,
        params string[] allowedKeys)
    {
        var allowed = new HashSet<string>(allowedKeys, StringComparer.OrdinalIgnoreCase);

        foreach (var key in parameters.Keys)
        {
            if (!allowed.Contains(key))
            {
                result.AddError($"Unused parameter '{key}'.");
            }
        }
    }

    public static string? RequireString(
        Dictionary<string, object?> parameters,
        ValidationResult result,
        string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var stringValue = value.ToString();
        if (string.IsNullOrWhiteSpace(stringValue))
        {
            result.AddError($"Parameter '{key}' must be a non-empty string.");
            return null;
        }

        return stringValue;
    }

    public static DateTime? RequireDate(
        Dictionary<string, object?> parameters,
        ValidationResult result,
        string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            result.AddError($"Parameter '{key}' is required.");
            return null;
        }

        return ParseIsoDate(value, result, key, isRequired: true);
    }

    public static int? OptionalInt(
        Dictionary<string, object?> parameters,
        ValidationResult result,
        string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (int.TryParse(value.ToString(), out var parsed))
        {
            return parsed;
        }

        result.AddError($"Parameter '{key}' must be an integer.");
        return null;
    }

    public static DateTime? OptionalDate(
        Dictionary<string, object?> parameters,
        ValidationResult result,
        string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return ParseIsoDate(value, result, key, isRequired: false);
    }

    public static void RequireEnvironment(
        Dictionary<string, object?> parameters,
        ValidationResult result)
    {
        var value = RequireString(parameters, result, "environment");
        if (value is null)
        {
            return;
        }

        if (!value.Equals("sandbox", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("production", StringComparison.OrdinalIgnoreCase))
        {
            result.AddError("Parameter 'environment' must be 'sandbox' or 'production'.");
        }
    }

    private static DateTime? ParseIsoDate(
        object value,
        ValidationResult result,
        string key,
        bool isRequired)
    {
        var text = value.ToString();

        if (string.IsNullOrWhiteSpace(text))
        {
            if (isRequired)
            {
                result.AddError($"Parameter '{key}' is required.");
            }
            else
            {
                result.AddError($"Parameter '{key}' must be a valid ISO-8601 date.");
            }

            return null;
        }

        if (DateTime.TryParseExact(
                text,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        result.AddError($"Parameter '{key}' must be a valid ISO-8601 date in yyyy-MM-dd format.");
        return null;
    }
}
