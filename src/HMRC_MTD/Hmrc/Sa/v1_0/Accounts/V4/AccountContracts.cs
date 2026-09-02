using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.Sa.v1_0.Accounts.V4;

public static class AccountEndpoints
{
    private const string Accept = "application/vnd.hmrc.4.0+json";
    private static readonly EndpointParameter[] Nino = [new("nino")];
    public static readonly HmrcEndpoint BalanceAndTransactions = new("Retrieve balance and transactions", "GET", "/accounts/self-assessment/{nino}/balance-and-transactions", "4.0", Accept, "read:self-assessment", 200, Nino, [new("docNumber", false), new("fromDate", false), new("toDate", false), new("onlyOpenItems", false), new("includeLocks", false), new("calculateAccruedInterest", false), new("removePOA", false), new("customerPaymentInformation", false), new("includeEstimatedCharges", false)], false, ResponseType: typeof(BalanceAndTransactionsResponse));
    public static readonly HmrcEndpoint PaymentsAndAllocations = new("Retrieve payments and allocations", "GET", "/accounts/self-assessment/{nino}/payments-and-allocations", "4.0", Accept, "read:self-assessment", 200, Nino, [new("fromDate", false), new("toDate", false), new("paymentLot", false), new("paymentLotItem", false)], false, ResponseType: typeof(PaymentsAndAllocationsResponse));
    public static IReadOnlyList<HmrcEndpoint> All => [BalanceAndTransactions, PaymentsAndAllocations];
}

public sealed record BalanceAndTransactionsQuery(string? DocNumber = null, DateOnly? FromDate = null, DateOnly? ToDate = null, bool? OnlyOpenItems = null, bool? IncludeLocks = null, bool? CalculateAccruedInterest = null, bool? RemovePoa = null, bool? CustomerPaymentInformation = null, bool? IncludeEstimatedCharges = null);
public sealed record PaymentsAndAllocationsQuery(DateOnly? FromDate = null, DateOnly? ToDate = null, string? PaymentLot = null, string? PaymentLotItem = null);

public sealed class BalanceAndTransactionsResponse : HmrcResponse
{
    [JsonPropertyName("balanceDetails")] public AccountBalanceDetails? BalanceDetails { get; set; }
    [JsonPropertyName("codingDetails")] public List<CodingDetail>? CodingDetails { get; set; }
    [JsonPropertyName("documentDetails")] public List<DocumentDetail>? DocumentDetails { get; set; }
    [JsonPropertyName("financialDetails")] public List<FinancialDetail>? FinancialDetails { get; set; }
}

public sealed class AccountBalanceDetails : HmrcResponse
{
    [JsonPropertyName("payableAmount")] public decimal? PayableAmount { get; set; }
    [JsonPropertyName("payableDueDate")] public DateOnly? PayableDueDate { get; set; }
    [JsonPropertyName("pendingChargeDueAmount")] public decimal? PendingChargeDueAmount { get; set; }
    [JsonPropertyName("pendingChargeDueDate")] public DateOnly? PendingChargeDueDate { get; set; }
    [JsonPropertyName("overdueAmount")] public decimal? OverdueAmount { get; set; }
    [JsonPropertyName("bcdBalancePerYear")] public List<BcdBalance>? BcdBalancePerYear { get; set; }
    [JsonPropertyName("earliestPaymentDateOverdue")] public DateOnly? EarliestPaymentDateOverdue { get; set; }
    [JsonPropertyName("totalBalance")] public decimal? TotalBalance { get; set; }
    [JsonPropertyName("amountCodedOut")] public decimal? AmountCodedOut { get; set; }
    [JsonPropertyName("totalBcdBalance")] public decimal? TotalBcdBalance { get; set; }
    [JsonPropertyName("unallocatedCredit")] public decimal? UnallocatedCredit { get; set; }
    [JsonPropertyName("allocatedCredit")] public decimal? AllocatedCredit { get; set; }
    [JsonPropertyName("totalCredit")] public decimal? TotalCredit { get; set; }
    [JsonPropertyName("firstPendingAmountRequested")] public decimal? FirstPendingAmountRequested { get; set; }
    [JsonPropertyName("secondPendingAmountRequested")] public decimal? SecondPendingAmountRequested { get; set; }
    [JsonPropertyName("availableCredit")] public decimal? AvailableCredit { get; set; }
}

public sealed class BcdBalance
{
    [JsonPropertyName("taxYear")] public required string TaxYear { get; set; }
    [JsonPropertyName("bcdAmount")] public required decimal BcdAmount { get; set; }
}

public sealed class CodingDetail : HmrcResponse
{
    [JsonPropertyName("returnTaxYear")] public string? ReturnTaxYear { get; set; }
    [JsonPropertyName("totalLiabilityAmount")] public decimal? TotalLiabilityAmount { get; set; }
    [JsonPropertyName("codingTaxYear")] public string? CodingTaxYear { get; set; }
    [JsonPropertyName("coded")] public CodedAmount? Coded { get; set; }
}

public sealed class CodedAmount
{
    [JsonPropertyName("charge")] public decimal? Charge { get; set; }
    [JsonPropertyName("initiationDate")] public DateOnly? InitiationDate { get; set; }
}

public sealed class DocumentDetail : HmrcResponse
{
    [JsonPropertyName("taxYear")] public string? TaxYear { get; set; }
    [JsonPropertyName("documentId")] public string? DocumentId { get; set; }
    [JsonPropertyName("formBundleNumber")] public string? FormBundleNumber { get; set; }
    [JsonPropertyName("creditReason")] public string? CreditReason { get; set; }
    [JsonPropertyName("documentDate")] public DateOnly? DocumentDate { get; set; }
    [JsonPropertyName("documentDescription")] public string? DocumentDescription { get; set; }
    [JsonPropertyName("documentText")] public string? DocumentText { get; set; }
    [JsonPropertyName("chargeClassification")] public string? ChargeClassification { get; set; }
    [JsonPropertyName("originalAmount")] public decimal? OriginalAmount { get; set; }
    [JsonPropertyName("outstandingAmount")] public decimal? OutstandingAmount { get; set; }
    [JsonPropertyName("documentDueDate")] public DateOnly? DocumentDueDate { get; set; }
    [JsonPropertyName("lastClearing")] public LastClearing? LastClearing { get; set; }
    [JsonPropertyName("isChargeEstimate")] public bool? IsChargeEstimate { get; set; }
    [JsonPropertyName("isCodedOut")] public bool? IsCodedOut { get; set; }
    [JsonPropertyName("paymentLot")] public string? PaymentLot { get; set; }
    [JsonPropertyName("paymentLotItem")] public string? PaymentLotItem { get; set; }
    [JsonPropertyName("effectiveDateOfPayment")] public DateOnly? EffectiveDateOfPayment { get; set; }
    [JsonPropertyName("latePaymentInterest")] public LatePaymentInterest? LatePaymentInterest { get; set; }
    [JsonPropertyName("amountCodedOut")] public decimal? AmountCodedOut { get; set; }
    [JsonPropertyName("reducedCharge")] public ReducedCharge? ReducedCharge { get; set; }
    [JsonPropertyName("poaRelevantAmount")] public decimal? PoaRelevantAmount { get; set; }
}

public sealed class LastClearing
{
    [JsonPropertyName("lastClearingDate")] public DateOnly? LastClearingDate { get; set; }
    [JsonPropertyName("lastClearingReason")] public string? LastClearingReason { get; set; }
    [JsonPropertyName("lastClearedAmount")] public decimal? LastClearedAmount { get; set; }
}

public sealed class LatePaymentInterest
{
    [JsonPropertyName("latePaymentInterestId")] public string? LatePaymentInterestId { get; set; }
    [JsonPropertyName("accruingInterestAmount")] public decimal? AccruingInterestAmount { get; set; }
    [JsonPropertyName("interestRate")] public decimal? InterestRate { get; set; }
    [JsonPropertyName("interestStartDate")] public DateOnly? InterestStartDate { get; set; }
    [JsonPropertyName("interestEndDate")] public DateOnly? InterestEndDate { get; set; }
    [JsonPropertyName("interestAmount")] public decimal? InterestAmount { get; set; }
    [JsonPropertyName("interestDunningLockAmount")] public decimal? InterestDunningLockAmount { get; set; }
    [JsonPropertyName("interestOutstandingAmount")] public decimal? InterestOutstandingAmount { get; set; }
}

public sealed class ReducedCharge
{
    [JsonPropertyName("chargeType")] public string? ChargeType { get; set; }
    [JsonPropertyName("documentNumber")] public string? DocumentNumber { get; set; }
    [JsonPropertyName("amendmentDate")] public DateOnly? AmendmentDate { get; set; }
    [JsonPropertyName("taxYear")] public string? TaxYear { get; set; }
}

public sealed class FinancialDetail : HmrcResponse
{
    [JsonPropertyName("taxYear")] public string? TaxYear { get; set; }
    [JsonPropertyName("chargeDetail")] public AccountChargeDetail? ChargeDetail { get; set; }
    [JsonPropertyName("taxPeriodFrom")] public DateOnly? TaxPeriodFrom { get; set; }
    [JsonPropertyName("taxPeriodTo")] public DateOnly? TaxPeriodTo { get; set; }
    [JsonPropertyName("contractAccount")] public string? ContractAccount { get; set; }
    [JsonPropertyName("documentNumber")] public string? DocumentNumber { get; set; }
    [JsonPropertyName("documentNumberItem")] public string? DocumentNumberItem { get; set; }
    [JsonPropertyName("chargeReference")] public string? ChargeReference { get; set; }
    [JsonPropertyName("originalAmount")] public decimal? OriginalAmount { get; set; }
    [JsonPropertyName("outstandingAmount")] public decimal? OutstandingAmount { get; set; }
    [JsonPropertyName("clearedAmount")] public decimal? ClearedAmount { get; set; }
    [JsonPropertyName("accruedInterest")] public decimal? AccruedInterest { get; set; }
    [JsonPropertyName("items")] public List<FinancialItem>? Items { get; set; }
}

public sealed class AccountChargeDetail
{
    [JsonPropertyName("documentId")] public string? DocumentId { get; set; }
    [JsonPropertyName("documentType")] public string? DocumentType { get; set; }
    [JsonPropertyName("documentTypeDescription")] public string? DocumentTypeDescription { get; set; }
    [JsonPropertyName("chargeType")] public string? ChargeType { get; set; }
    [JsonPropertyName("chargeTypeDescription")] public string? ChargeTypeDescription { get; set; }
}

public sealed class FinancialItem : HmrcResponse
{
    [JsonPropertyName("itemId")] public string? ItemId { get; set; }
    [JsonPropertyName("amount")] public decimal? Amount { get; set; }
    [JsonPropertyName("dueDate")] public DateOnly? DueDate { get; set; }
    [JsonPropertyName("clearingDate")] public DateOnly? ClearingDate { get; set; }
    [JsonPropertyName("clearingReason")] public string? ClearingReason { get; set; }
    [JsonPropertyName("outgoingPaymentMethod")] public string? OutgoingPaymentMethod { get; set; }
    [JsonPropertyName("locks")] public AccountLocks? Locks { get; set; }
    [JsonPropertyName("isReturn")] public bool? IsReturn { get; set; }
    [JsonPropertyName("paymentReference")] public string? PaymentReference { get; set; }
    [JsonPropertyName("paymentAmount")] public decimal? PaymentAmount { get; set; }
    [JsonPropertyName("paymentMethod")] public string? PaymentMethod { get; set; }
    [JsonPropertyName("paymentLot")] public string? PaymentLot { get; set; }
    [JsonPropertyName("paymentLotItem")] public string? PaymentLotItem { get; set; }
    [JsonPropertyName("clearingSAPDocument")] public string? ClearingSapDocument { get; set; }
    [JsonPropertyName("isChargeEstimate")] public bool? IsChargeEstimate { get; set; }
    [JsonPropertyName("codedOutStatus")] public string? CodedOutStatus { get; set; }
}

public sealed class AccountLocks
{
    [JsonPropertyName("isChargeOnHold")] public required bool IsChargeOnHold { get; set; }
    [JsonPropertyName("isEstimatedChargeOnHold")] public required bool IsEstimatedChargeOnHold { get; set; }
    [JsonPropertyName("isInterestAccrualOnHold")] public required bool IsInterestAccrualOnHold { get; set; }
    [JsonPropertyName("isInterestChargeOnHold")] public required bool IsInterestChargeOnHold { get; set; }
}

public sealed class PaymentsAndAllocationsResponse : HmrcResponse
{
    [JsonPropertyName("payments")] public required List<AccountPayment> Payments { get; set; }
}

public sealed class AccountPayment : HmrcResponse
{
    [JsonPropertyName("paymentLot")] public string? PaymentLot { get; set; }
    [JsonPropertyName("paymentLotItem")] public string? PaymentLotItem { get; set; }
    [JsonPropertyName("paymentReference")] public string? PaymentReference { get; set; }
    [JsonPropertyName("paymentAmount")] public decimal? PaymentAmount { get; set; }
    [JsonPropertyName("paymentMethod")] public string? PaymentMethod { get; set; }
    [JsonPropertyName("transactionDate")] public DateOnly? TransactionDate { get; set; }
    [JsonPropertyName("allocations")] public List<PaymentAllocation>? Allocations { get; set; }
}

public sealed class PaymentAllocation : HmrcResponse
{
    [JsonPropertyName("chargeReference")] public required string ChargeReference { get; set; }
    [JsonPropertyName("periodKey")] public string? PeriodKey { get; set; }
    [JsonPropertyName("periodKeyDescription")] public string? PeriodKeyDescription { get; set; }
    [JsonPropertyName("startDate")] public DateOnly? StartDate { get; set; }
    [JsonPropertyName("endDate")] public DateOnly? EndDate { get; set; }
    [JsonPropertyName("dueDate")] public DateOnly? DueDate { get; set; }
    [JsonPropertyName("chargeDetail")] public AccountChargeDetail? ChargeDetail { get; set; }
    [JsonPropertyName("amount")] public decimal? Amount { get; set; }
    [JsonPropertyName("clearedAmount")] public decimal? ClearedAmount { get; set; }
    [JsonPropertyName("contractAccount")] public string? ContractAccount { get; set; }
}
