namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

public interface IRequestValidator
{
    ValidationResult Validate(Dictionary<string, object?> parameters);
}
