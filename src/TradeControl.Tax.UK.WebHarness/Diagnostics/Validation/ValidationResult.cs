namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

public sealed class ValidationResult
{
    private readonly List<string> _warnings = new();
    private readonly List<string> _errors = new();

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyList<string> Warnings => _warnings;

    public IReadOnlyList<string> Errors => _errors;

    public void AddWarning(string warning) => _warnings.Add(warning);

    public void AddError(string error) => _errors.Add(error);
}
