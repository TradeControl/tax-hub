using System.Globalization;
using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.ViewReturn;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed record VatReturnDifference(string Field, string PreparedValue, string AuthorityValue);

public sealed record VatReturnReconciliationResult(
    string PreparedBodySha256,
    string SourceDataset,
    string SourceSnapshotToken,
    string PeriodKey,
    IReadOnlyList<VatReturnDifference> Differences)
{
    public bool Matches => Differences.Count == 0;
}

/// <summary>
/// Compares HMRC's view of a VAT return with the exact prepared artifact derived from
/// Cash.vwTaxVatSubmission. It does not recalculate, round or otherwise normalise a box.
/// </summary>
public static class VatReturnReconciliation
{
    private const string VatDataset = "Cash.vwTaxVatSubmission";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static VatReturnReconciliationResult Compare(PreparedApiRequest prepared,
        ReadOnlySpan<byte> authorityResponse)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.OperationId != "vat.returns.submit" || prepared.Method != "POST"
            || prepared.BodyBytes is not { } body || string.IsNullOrWhiteSpace(prepared.BodySha256))
            throw new InvalidOperationException("An exact prepared VAT submission is required for reconciliation.");
        var evidence = prepared.SourceEvidence.SingleOrDefault(item =>
            item.DatasetKey.Equals(VatDataset, StringComparison.Ordinal));
        if (evidence is null || string.IsNullOrWhiteSpace(evidence.SnapshotToken))
            throw new InvalidOperationException("The prepared VAT submission lacks its authoritative source snapshot.");

        VatReturnRequest submitted;
        VatViewReturnResponse authority;
        try
        {
            submitted = JsonSerializer.Deserialize<VatReturnRequest>(body.AsSpan(), Json)
                ?? throw new JsonException();
            authority = JsonSerializer.Deserialize<VatViewReturnResponse>(authorityResponse, Json)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The prepared or authority VAT return is not valid JSON.", exception);
        }

        var differences = new List<VatReturnDifference>();
        CompareText("periodKey", submitted.PeriodKey, authority.PeriodKey ?? string.Empty);
        CompareAmount("vatDueSales", submitted.VatDueSales, authority.VatDueSales);
        CompareAmount("vatDueAcquisitions", submitted.VatDueAcquisitions, authority.VatDueAcquisitions);
        CompareAmount("totalVatDue", submitted.TotalVatDue, authority.TotalVatDue);
        CompareAmount("vatReclaimedCurrPeriod", submitted.VatReclaimedCurrPeriod, authority.VatReclaimedCurrPeriod);
        CompareAmount("netVatDue", submitted.NetVatDue, authority.NetVatDue);
        CompareAmount("totalValueSalesExVAT", submitted.TotalValueSalesExVat, authority.TotalValueSalesExVat);
        CompareAmount("totalValuePurchasesExVAT", submitted.TotalValuePurchasesExVat, authority.TotalValuePurchasesExVat);
        CompareAmount("totalValueGoodsSuppliedExVAT", submitted.TotalValueGoodsSuppliedExVat,
            authority.TotalValueGoodsSuppliedExVat);
        CompareAmount("totalAcquisitionsExVAT", submitted.TotalAcquisitionsExVat, authority.TotalAcquisitionsExVat);

        return new(prepared.BodySha256, evidence.DatasetKey, evidence.SnapshotToken,
            submitted.PeriodKey, differences);

        void CompareText(string field, string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                differences.Add(new(field, expected, actual));
        }

        void CompareAmount(string field, decimal expected, decimal actual)
        {
            if (expected != actual)
                differences.Add(new(field, Text(expected), Text(actual)));
        }
    }

    private static string Text(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}
