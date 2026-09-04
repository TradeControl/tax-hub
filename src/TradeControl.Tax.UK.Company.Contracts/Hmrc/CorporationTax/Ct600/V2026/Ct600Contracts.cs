using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Statutory;

namespace TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026;

public sealed record Ct600A(
    decimal LoansOutstandingAtPeriodEnd,
    decimal TaxChargeable,
    decimal TaxPaid);

public sealed record Ct600Return(
    string CompanyName,
    string CompanyRegistrationNumber,
    string Utr,
    ReportingPeriod Period,
    decimal Turnover,
    decimal ProfitBeforeTax,
    decimal TaxableTotalProfits,
    decimal CorporationTaxChargeable,
    decimal TaxPayable,
    bool AccountsAttached,
    bool ComputationsAttached,
    Ct600A? SupplementaryPageA,
    string DeclarantName,
    DateOnly DeclarationDate);

public static class Ct600Contract
{
    public const string Namespace = "http://www.govtalk.gov.uk/taxation/CT/5";
    public const string RimVersion = "1.994";
    public static ContractStatus Status => ContractStatus.Production;
}
