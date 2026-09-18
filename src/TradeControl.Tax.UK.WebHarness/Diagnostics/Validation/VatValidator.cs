namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

public sealed class VatValidator : IRequestValidator
{
    public ValidationResult Validate(Dictionary<string, object?> parameters)
    {
        var result = new ValidationResult();

        ValidatorHelpers.RequireKeys(parameters, result,
            "taxSourceCode", "periodEndOn", "tenantId", "subjectId", "connectionString", "environment");

        ValidatorHelpers.RejectUnusedKeys(parameters, result,
            "taxSourceCode", "periodEndOn", "tenantId", "subjectId", "connectionString", "environment");

        ValidatorHelpers.RequireString(parameters, result, "taxSourceCode");
        ValidatorHelpers.RequireString(parameters, result, "tenantId");
        ValidatorHelpers.RequireString(parameters, result, "subjectId");
        ValidatorHelpers.RequireString(parameters, result, "connectionString");
        ValidatorHelpers.RequireEnvironment(parameters, result);

        ValidatorHelpers.RequireDate(parameters, result, "periodEndOn");

        return result;
    }
}
