namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

public sealed class LiabilityValidator : IRequestValidator
{
    public ValidationResult Validate(Dictionary<string, object?> parameters)
    {
        var result = new ValidationResult();

        ValidatorHelpers.RequireKeys(parameters, result,
            "tenantId", "subjectId", "limit", "environment");

        ValidatorHelpers.RejectUnusedKeys(parameters, result,
            "tenantId", "subjectId", "limit", "dateFrom", "dateTo", "environment");

        ValidatorHelpers.RequireString(parameters, result, "tenantId");
        ValidatorHelpers.RequireString(parameters, result, "subjectId");
        ValidatorHelpers.RequireEnvironment(parameters, result);

        var limit = ValidatorHelpers.OptionalInt(parameters, result, "limit");
        if (limit is null || limit <= 0)
        {
            result.AddError("Parameter 'limit' must be a positive integer.");
        }

        var dateFrom = ValidatorHelpers.OptionalDate(parameters, result, "dateFrom");
        var dateTo = ValidatorHelpers.OptionalDate(parameters, result, "dateTo");

        if ((dateFrom is null) != (dateTo is null))
        {
            result.AddError("Parameters 'dateFrom' and 'dateTo' must be supplied together.");
        }

        return result;
    }
}
