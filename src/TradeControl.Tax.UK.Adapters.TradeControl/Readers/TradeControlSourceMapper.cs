using System.Security.Cryptography;
using System.Text;
using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public static class TradeControlSourceMapper
{
    private const string SourceSystem = "TradeControl";

    public static TaxSubject Subject(StatutoryContextSnapshot context)
    {
        var identity = context.Identity;
        return new(identity.SubjectCode, identity.SubjectName,
            identity.BusinessTaxTypeCode == 4 ? TaxSubjectKind.Person : TaxSubjectKind.Organisation,
            identity.BusinessTaxTypeCode switch { 0 => TaxLegalForm.Company, 4 => TaxLegalForm.SoleTrader, _ => TaxLegalForm.Other },
            identity.JurisdictionCode, identity.CurrencyCode);
    }

    public static VatReturnSource Vat(TaxSubject subject, TaxReportingPeriod period, TcVatProjectionRow row, string snapshotToken)
    {
        if (row.PeriodStart != period.Start || row.PeriodEnd != period.End)
            throw new InvalidOperationException("The VAT projection period does not match the requested period.");
        var keys = new[] { "VAT-DUE-SALES", "VAT-DUE-ACQUISITIONS", "VAT-ADJUSTMENT", "TOTAL-VAT-DUE", "VAT-RECLAIMED",
            "NET-VAT-DUE", "SALES-EX-VAT", "PURCHASES-EX-VAT", "GOODS-SUPPLIED-EX-VAT", "ACQUISITIONS-EX-VAT" };
        var values = new[] { row.VatDueSales, row.VatDueAcquisitions, row.VatAdjustment, row.TotalVatDue, row.VatReclaimedCurrentPeriod,
            row.NetVatDue, row.TotalValueSalesExVat, row.TotalValuePurchasesExVat,
            row.TotalValueGoodsSuppliedExVat, row.TotalAcquisitionsExVat };
        var provenance = keys.Select(key => new FactProvenance(SourceSystem, "Cash.vwTaxVatSubmission", key)).ToArray();
        TaxFact<decimal> Fact(int index) => new(keys[index], keys[index], Value(values[index]), provenance[index]);
        return new(subject, period, Fact(0), Fact(1), Fact(2), Fact(3), Fact(4), Fact(5), Fact(6), Fact(7), Fact(8), Fact(9),
            new(SourceSystem, "Cash.vwTaxVatSubmission", "1", DateTimeOffset.UtcNow, snapshotToken, provenance));
    }

    public static BusinessIncomeSource Business(TaxSubject subject, BusinessId businessId, TaxSourceCode source,
        TaxReportingPeriod period, IReadOnlyList<TcBusinessProjectionRow> rows,
        IReadOnlyList<TcContributorRow> contributors)
    {
        if (rows.Count == 0) throw new InvalidOperationException($"Tax source '{source}' is not configured.");
        if (rows.Any(row => row.TaxSourceCode != source.Value || row.PeriodStart != period.Start
            || row.PeriodEndExclusive != period.End.AddDays(1)))
            throw new InvalidOperationException("The cumulative projection does not match the requested source and period.");
        var duplicate = rows.GroupBy(row => row.TagCode, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate cumulative fact '{duplicate.Key}'.");

        var facts = rows.Select(row =>
        {
            var factProvenance = new FactProvenance(SourceSystem, "Cash.fnTaxBizCumulative", row.TagCode,
                string.Join(',', contributors.Where(item => item.TagCode.Equals(row.TagCode, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.CashCode).Distinct(StringComparer.OrdinalIgnoreCase).Order()));
            var state = row.ValidationStatus.Equals("Invalid", StringComparison.OrdinalIgnoreCase)
                ? TaxValue<decimal>.Invalid("The authoritative cumulative projection is invalid.")
                : row.SupportStatus.ToUpperInvariant() switch
                {
                    "SUPPORTED" when row.StatutoryAmount.HasValue => Value(row.StatutoryAmount.Value),
                    "UNSUPPORTED" => TaxValue<decimal>.Unsupported("The configured source does not support this fact."),
                    "INVALID" => TaxValue<decimal>.Invalid("The configured mapping is invalid."),
                    _ => TaxValue<decimal>.Absent("The authoritative projection returned no amount.")
                };
            return new BusinessIncomeFact(new(row.TagCode), row.TagName,
                row.CashPolarityCode == 1 ? TaxFactKind.Income : TaxFactKind.Expense, state, factProvenance);
        }).ToArray();
        var tokenText = string.Join('|', contributors.OrderBy(x => x.TagCode).ThenBy(x => x.CashCode).ThenBy(x => x.PeriodStart)
            .Select(x => $"{x.TagCode}:{x.CashCode}:{x.PeriodStart:yyyy-MM-dd}:{x.CashCodeRowVersion}:{x.PeriodRowVersion}"));
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenText)));
        return new(subject, businessId, source, period, facts,
            new(SourceSystem, "Cash.fnTaxBizCumulative", "1", DateTimeOffset.UtcNow, token,
                facts.Select(x => x.Provenance).ToArray()));
    }

    private static TaxValue<decimal> Value(decimal value) => value == 0m
        ? TaxValue<decimal>.ExplicitZero(0m)
        : TaxValue<decimal>.Present(value);
}
