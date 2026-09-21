using System.Text.RegularExpressions;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Obligations.V3;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;
using TradeControl.Tax.UK.Hmrc.Vat;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed record DescribeVatObligations(
    string Vrn,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Status = null);

public sealed record DescribeVatReturn(string Vrn, string PeriodKey);

public sealed record DescribeIncomeTaxObligations(
    string Nino,
    string? TypeOfBusiness = null,
    string? BusinessId = null,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    string? Status = null);

public sealed class BodylessRequestDescriber(PreparedApiRequestPipeline pipeline)
{
    private static readonly Regex Vrn = new("^[0-9]{9}$", RegexOptions.Compiled);
    private static readonly Regex PeriodKey = new("^[A-Za-z0-9]{4}$", RegexOptions.Compiled);
    private static readonly Regex Nino = new("^[A-Z]{2}[0-9]{6}[A-D]$", RegexOptions.Compiled);
    private static readonly Regex BusinessId = new("^X[A-Z0-9]{1}IS[0-9]{11}$", RegexOptions.Compiled);

    public PreparedApiRequest Describe(DescribeVatObligations request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vrn = NormalizeVrn(request.Vrn);
        ValidateDatePair(request.From, request.To, "from", "to");
        var status = OptionalChoice(request.Status, "status", "O", "F");
        var descriptor = VatOperationCatalog.All.Single(item => item.OperationId == "vat.obligations.list");

        return pipeline.Prepare(HmrcPreparedApiContracts.From(descriptor), [new("vrn", vrn)],
            Optional(
                ("from", Iso(request.From)),
                ("to", Iso(request.To)),
                ("status", status)));
    }

    public PreparedApiRequest Describe(DescribeVatReturn request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vrn = NormalizeVrn(request.Vrn);
        var periodKey = (request.PeriodKey ?? string.Empty).Trim();
        if (!PeriodKey.IsMatch(periodKey))
            throw new ArgumentException("VAT periodKey must contain exactly four letters or digits.", nameof(request));
        var descriptor = VatOperationCatalog.All.Single(item => item.OperationId == "vat.returns.retrieve");
        return pipeline.Prepare(HmrcPreparedApiContracts.From(descriptor),
            [new("vrn", vrn), new("periodKey", periodKey)]);
    }

    public PreparedApiRequest Describe(DescribeIncomeTaxObligations request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nino = NormalizeNino(request.Nino);
        var type = OptionalChoice(request.TypeOfBusiness, "typeOfBusiness", "self-employment");
        var businessId = string.IsNullOrWhiteSpace(request.BusinessId) ? null : request.BusinessId.Trim().ToUpperInvariant();
        if (businessId is not null && !BusinessId.IsMatch(businessId))
            throw new ArgumentException("businessId is not a valid self-employment business identifier.", nameof(request));
        if ((type is null) != (businessId is null))
            throw new ArgumentException("typeOfBusiness and businessId must either both be supplied or both be omitted.", nameof(request));
        ValidateDatePair(request.FromDate, request.ToDate, "fromDate", "toDate");
        var status = OptionalChoice(request.Status, "status", "Open", "Fulfilled");
        var coverage = SaOperationCatalog.Coverage.Single(item =>
            ReferenceEquals(item.Descriptor, ObligationEndpoints.IncomeAndExpenditure));

        return pipeline.Prepare(HmrcPreparedApiContracts.From(coverage), [new("nino", nino)],
            Optional(
                ("typeOfBusiness", type),
                ("businessId", businessId),
                ("fromDate", Iso(request.FromDate)),
                ("toDate", Iso(request.ToDate)),
                ("status", status)));
    }

    private static string NormalizeVrn(string? value)
    {
        var result = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!Vrn.IsMatch(result))
            throw new ArgumentException("vrn must contain exactly nine digits.", nameof(value));
        return result;
    }

    private static string NormalizeNino(string? value)
    {
        var result = new string((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))
            .Select(char.ToUpperInvariant).ToArray());
        if (!Nino.IsMatch(result))
            throw new ArgumentException("nino must contain two letters, six digits and a final letter A-D.", nameof(value));
        return result;
    }

    private static void ValidateDatePair(DateOnly? from, DateOnly? to, string fromName, string toName)
    {
        if (from.HasValue != to.HasValue)
            throw new ArgumentException($"{fromName} and {toName} must either both be supplied or both be omitted.");
        if (from > to)
            throw new ArgumentException($"{fromName} cannot be after {toName}.");
    }

    private static string? OptionalChoice(string? value, string name, params string[] choices)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var choice = choices.SingleOrDefault(item => item.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return choice ?? throw new ArgumentException($"{name} must be one of: {string.Join(", ", choices)}.", name);
    }

    private static string? Iso(DateOnly? value) => value?.ToString("yyyy-MM-dd");

    private static PreparedNameValue[] Optional(params (string Name, string? Value)[] values) => values
        .Where(item => item.Value is not null)
        .Select(item => new PreparedNameValue(item.Name, item.Value!))
        .ToArray();
}
