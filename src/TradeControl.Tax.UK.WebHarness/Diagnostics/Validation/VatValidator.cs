using TradeControl.Tax.UK.Adapters.TradeControl.Data;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

public sealed class VatValidator : IRequestValidator
{
    private readonly TcVatReader _reader;

    public VatValidator(TcVatReader reader)
    {
        _reader = reader;
    }

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

        var periodEndOn = ValidatorHelpers.RequireDate(parameters, result, "periodEndOn");
        var connectionString = ValidatorHelpers.RequireString(parameters, result, "connectionString");

        if (result.IsValid && periodEndOn is not null && connectionString is not null)
        {
            var row = _reader.ReadAsync(connectionString, periodEndOn.Value).GetAwaiter().GetResult();
            if (row is null)
            {
                result.AddError("No VAT dataset row was found for the requested period.");
            }
        }

        return result;
    }
}
