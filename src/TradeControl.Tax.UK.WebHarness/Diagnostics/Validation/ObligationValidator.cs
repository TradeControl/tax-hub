namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

public sealed class ObligationValidator : IRequestValidator
{
    public ValidationResult Validate(Dictionary<string, object?> parameters)
    {
        var result = new ValidationResult();

        ValidatorHelpers.RequireKeys(parameters, result,
            "tenantId", "subjectId", "obligationStatus", "environment");

        ValidatorHelpers.RejectUnusedKeys(parameters, result,
            "tenantId", "subjectId", "obligationStatus", "environment");

        ValidatorHelpers.RequireString(parameters, result, "tenantId");
        ValidatorHelpers.RequireString(parameters, result, "subjectId");
        ValidatorHelpers.RequireEnvironment(parameters, result);

        var status = ValidatorHelpers.RequireString(parameters, result, "obligationStatus");
        if (status is not null
            && !status.Equals("open", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("fulfilled", StringComparison.OrdinalIgnoreCase))
        {
            result.AddError("Parameter 'obligationStatus' must be 'open' or 'fulfilled'.");
        }

        return result;
    }
}
