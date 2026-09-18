namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed record TcVatProjectionRow(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal VatDueSales,
    decimal VatDueAcquisitions,
    decimal VatAdjustment,
    decimal TotalVatDue,
    decimal VatReclaimedCurrentPeriod,
    decimal NetVatDue,
    decimal TotalValueSalesExVat,
    decimal TotalValuePurchasesExVat,
    decimal TotalValueGoodsSuppliedExVat,
    decimal TotalAcquisitionsExVat);

public sealed record TcBusinessProjectionRow(
    string TaxSourceCode,
    DateOnly PeriodStart,
    DateOnly PeriodEndExclusive,
    string ValidationStatus,
    string TagCode,
    string TagName,
    short CashPolarityCode,
    string SupportStatus,
    decimal? StatutoryAmount);

public sealed record TcContributorRow(
    string TagCode,
    string CashCode,
    DateOnly PeriodStart,
    string CashCodeRowVersion,
    string PeriodRowVersion);
