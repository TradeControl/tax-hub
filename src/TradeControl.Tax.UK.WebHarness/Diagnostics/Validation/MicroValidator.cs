using TradeControl.Tax.UK.Adapters.TradeControl.Data;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

public sealed class MicroValidator : IRequestValidator
{
    private readonly TcBusinessTaxReader _reader;

    public MicroValidator(TcBusinessTaxReader reader)
    {
        _reader = reader;
    }

    public ValidationResult Validate(Dictionary<string, object?> parameters)
    {
        var result = new ValidationResult();

        ValidatorHelpers.RequireKeys(parameters, result,
            "taxSourceCode", "periodTo", "tenantId", "subjectId", "connectionString", "environment");

        ValidatorHelpers.RejectUnusedKeys(parameters, result,
            "taxSourceCode", "periodTo", "tenantId", "subjectId", "connectionString", "environment");

        var taxSourceCode = ValidatorHelpers.RequireString(parameters, result, "taxSourceCode");
        var periodTo = ValidatorHelpers.RequireDate(parameters, result, "periodTo");
        ValidatorHelpers.RequireString(parameters, result, "tenantId");
        ValidatorHelpers.RequireString(parameters, result, "subjectId");
        var connectionString = ValidatorHelpers.RequireString(parameters, result, "connectionString");
        ValidatorHelpers.RequireEnvironment(parameters, result);

        if (result.IsValid && taxSourceCode is not null && periodTo is not null && connectionString is not null)
        {
            var rows = _reader.ReadAsync(connectionString, taxSourceCode, periodTo.Value).GetAwaiter().GetResult();
            if (rows.Count == 0)
            {
                result.AddError("No MICRO dataset rows were found for the requested period.");
            }
        }

        return result;
    }
}
