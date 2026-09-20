using System.Text.RegularExpressions;
using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.SelfEmployment.V5.Cumulative;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed record PrepareCumulativePeriodSummary(
    SourceKey Source,
    TaxSourceCode TaxSourceCode,
    TaxReportingPeriod Period,
    string TaxYear);

public enum CumulativeExpenseShape { Consolidated, Detailed }
public enum CumulativeTargetSection { Income, Expense }

public sealed record CumulativeFactMapping(
    string StableKey,
    CumulativeTargetSection Section,
    string TargetMember,
    decimal Orientation,
    bool Required,
    CumulativeExpenseShape? ExpenseShape);

public static class CumulativePopulationProfile
{
    public const string Version = "2026.1";
    public static IReadOnlyList<CumulativeFactMapping> Mappings { get; } =
    [
        Income("turnover", "turnover", true),
        Income("otherBusinessIncome", "other", true),
        Expense("consolidatedExpenses", "consolidatedExpenses", CumulativeExpenseShape.Consolidated),
        Expense("costOfGoods", "costOfGoods", CumulativeExpenseShape.Detailed),
        Expense("paymentsToSubcontractors", "paymentsToSubcontractors", CumulativeExpenseShape.Detailed),
        Expense("wagesAndStaffCosts", "wagesAndStaffCosts", CumulativeExpenseShape.Detailed),
        Expense("carVanTravelExpenses", "carVanTravelExpenses", CumulativeExpenseShape.Detailed),
        Expense("premisesRunningCosts", "premisesRunningCosts", CumulativeExpenseShape.Detailed),
        Expense("maintenanceCosts", "maintenanceCosts", CumulativeExpenseShape.Detailed),
        Expense("adminCosts", "adminCosts", CumulativeExpenseShape.Detailed),
        Expense("businessEntertainmentCosts", "businessEntertainmentCosts", CumulativeExpenseShape.Detailed),
        Expense("advertisingCosts", "advertisingCosts", CumulativeExpenseShape.Detailed),
        Expense("interestOnBankOtherLoans", "interestOnBankOtherLoans", CumulativeExpenseShape.Detailed),
        Expense("financeCharges", "financeCharges", CumulativeExpenseShape.Detailed),
        Expense("irrecoverableDebts", "irrecoverableDebts", CumulativeExpenseShape.Detailed),
        Expense("professionalFees", "professionalFees", CumulativeExpenseShape.Detailed),
        Expense("depreciation", "depreciation", CumulativeExpenseShape.Detailed),
        Expense("otherExpenses", "otherExpenses", CumulativeExpenseShape.Detailed)
    ];

    private static CumulativeFactMapping Income(string key, string member, bool required) =>
        new(key, CumulativeTargetSection.Income, member, 1m, required, null);
    private static CumulativeFactMapping Expense(string key, string member, CumulativeExpenseShape shape) =>
        new(key, CumulativeTargetSection.Expense, member, 1m, true, shape);
}

public sealed class CumulativePeriodSummaryPreparer
{
    private const decimal MaximumWireAmount = 99999999999.99m;
    private static readonly Regex Nino = new("^[A-Z]{2}[0-9]{6}[A-D]$", RegexOptions.Compiled);
    private static readonly Regex TaxYear = new("^(?<start>[0-9]{4})-(?<end>[0-9]{2})$", RegexOptions.Compiled);
    private readonly IBusinessIncomeSourceReader _source;
    private readonly ISourceReadinessEvaluator _readiness;
    private readonly ISourceStatutoryContextReader _statutory;
    private readonly ISelfEmploymentFilingContextReader _filing;
    private readonly PreparedApiRequestPipeline _pipeline;

    public CumulativePeriodSummaryPreparer(IBusinessIncomeSourceReader source,
        ISourceReadinessEvaluator readiness, ISourceStatutoryContextReader statutory,
        ISelfEmploymentFilingContextReader filing, PreparedApiRequestPipeline pipeline)
    {
        _source = source;
        _readiness = readiness;
        _statutory = statutory;
        _filing = filing;
        _pipeline = pipeline;
    }

    public async Task<PreparedApiRequest> PrepareAsync(
        PrepareCumulativePeriodSummary command, CancellationToken cancellationToken = default)
    {
        var findings = new List<PreparedArtifactFinding>();
        StatutoryContextSnapshot? statutory = null;
        SelfEmploymentFilingContext? filing = null;
        BusinessIncomeSource? source = null;
        try { statutory = await _statutory.ReadAsync(command.Source, command.Period.End, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { findings.Add(Error("ITSA-CONTEXT-UNAVAILABLE", exception.Message)); }
        if (statutory is not null)
        {
            try
            {
                filing = await _filing.ReadAsync(new(command.Source, statutory.Identity.SubjectCode,
                    command.TaxSourceCode, command.Period.End), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { findings.Add(Error("ITSA-FILING-CONTEXT-UNAVAILABLE", exception.Message)); }
        }
        if (filing is not null)
        {
            try
            {
                source = await _source.ReadAsync(new(command.Source, command.TaxSourceCode,
                    filing.BusinessId, command.Period), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { findings.Add(Error("ITSA-PERIOD-UNAVAILABLE", exception.Message)); }
        }
        if (source is not null)
        {
            try
            {
                var readiness = await _readiness.EvaluateAsync(new(command.Source, source.Subject,
                    source.Period, command.TaxSourceCode), cancellationToken);
                findings.AddRange(readiness.Findings.Select(Finding));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { findings.Add(Error("ITSA-READINESS-UNAVAILABLE", exception.Message)); }
        }

        if (filing is not null)
        {
            if (!Nino.IsMatch(NormalizeNino(filing.Nino)))
                findings.Add(Error("ITSA-NINO-INVALID", "An effective National Insurance number is required.", "nino"));
            if (!Regex.IsMatch(filing.BusinessId.Value, "^X[A-Z0-9]{1}IS[0-9]{11}$"))
                findings.Add(Error("ITSA-BUSINESS-ID-INVALID", "An effective HMRC self-employment business ID is required.", "businessId"));
            if (!filing.AccountingBasis.Equals("CASH", StringComparison.OrdinalIgnoreCase)
                && !filing.AccountingBasis.Equals("ACCRUAL", StringComparison.OrdinalIgnoreCase))
                findings.Add(Error("ITSA-ACCOUNTING-BASIS-INVALID", "The accounting basis must be CASH or ACCRUAL."));
            if (!filing.QuarterlyPeriodType.Equals("STANDARD", StringComparison.OrdinalIgnoreCase)
                && !filing.QuarterlyPeriodType.Equals("CALENDAR", StringComparison.OrdinalIgnoreCase))
                findings.Add(Error("ITSA-PERIOD-TYPE-INVALID", "The quarterly period type is unsupported."));
        }
        var taxYearStart = ValidateTaxYear(command.TaxYear, command.Period, findings);
        if (filing is not null && taxYearStart.HasValue)
            ValidateCumulativePeriod(filing, command.Period, taxYearStart.Value, findings);

        CumulativeSubmission? body = null;
        if (source is not null)
            try { body = Populate(source); }
            catch (InvalidOperationException exception) { findings.Add(Error("ITSA-SOURCE-INVALID", exception.Message)); }

        var coverage = SaOperationCatalog.Coverage.Single(item =>
            item.AccountsMode == SaAccountsModeDecision.Supported && item.Descriptor.HasRequestBody);
        var nino = NormalizeNino(filing?.Nino);
        var businessId = filing?.BusinessId.Value ?? "invalid";
        return _pipeline.Prepare(HmrcPreparedApiContracts.From(coverage),
            [new("nino", nino.Length == 0 ? "invalid" : nino), new("businessId", businessId),
                new("taxYear", command.TaxYear)],
            serializeBody: () => SaJson.SerializeCanonical(body!),
            sourceEvidence: source is null ? [] :
                [new(source.Provenance.SourceSystem, source.Provenance.DatasetKey, source.Provenance.SnapshotToken)],
            validationStages: [new("cumulative-source-context-and-readiness", () => findings)]);
    }

    public static CumulativeSubmission Populate(BusinessIncomeSource source)
    {
        var facts = source.Facts.ToDictionary(item => item.Key.Value, StringComparer.OrdinalIgnoreCase);
        var known = CumulativePopulationProfile.Mappings.Select(item => item.StableKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = facts.Keys.Where(key => !known.Contains(key)).ToArray();
        if (unknown.Length > 0) throw new InvalidOperationException(
            $"Unsupported cumulative fact key(s): {string.Join(", ", unknown)}.");
        var consolidated = Active("consolidatedExpenses");
        var detailed = CumulativePopulationProfile.Mappings.Where(item =>
            item.ExpenseShape == CumulativeExpenseShape.Detailed).Any(item => Active(item.StableKey));
        if (consolidated == detailed) throw new InvalidOperationException(
            consolidated ? "Consolidated and detailed expenses cannot be submitted together."
                : "No supported cumulative expense profile was found.");

        decimal Required(string key, decimal orientation, bool completeUnsupportedWithZero = false,
            bool allowNegative = true)
        {
            if (!facts.TryGetValue(key, out var fact))
                throw new InvalidOperationException($"Required cumulative fact '{key}' has no usable value.");
            if (!fact.Amount.HasValue)
            {
                if (completeUnsupportedWithZero && fact.Amount.State == TaxValueState.Unsupported) return 0m;
                throw new InvalidOperationException($"Required cumulative fact '{key}' has no usable value.");
            }
            var value = decimal.Round(fact.Amount.Value * orientation, 2, MidpointRounding.AwayFromZero);
            if ((!allowNegative && value < 0m) || value < -MaximumWireAmount || value > MaximumWireAmount)
                throw new InvalidOperationException($"Cumulative fact '{key}' is outside the supported wire range.");
            return value;
        }
        var income = new PeriodIncome
        {
            Turnover = Required("turnover", 1m, allowNegative: false),
            Other = Required("otherBusinessIncome", 1m, allowNegative: false)
        };
        PeriodExpenses expenses = consolidated
            ? new ConsolidatedPeriodExpenses { ConsolidatedExpenses = Required("consolidatedExpenses", 1m) }
            : Detailed();
        return new()
        {
            PeriodDates = new() { PeriodStartDate = source.Period.Start, PeriodEndDate = source.Period.End },
            PeriodIncome = income,
            PeriodExpenses = expenses
        };

        bool Active(string key) => facts.TryGetValue(key, out var fact)
            && fact.Amount.State != TaxValueState.Unsupported;
        DetailedPeriodExpenses Detailed() => new()
        {
            CostOfGoods = Required("costOfGoods", 1m, true),
            PaymentsToSubcontractors = Required("paymentsToSubcontractors", 1m, true),
            WagesAndStaffCosts = Required("wagesAndStaffCosts", 1m, true),
            CarVanTravelExpenses = Required("carVanTravelExpenses", 1m, true),
            PremisesRunningCosts = Required("premisesRunningCosts", 1m, true),
            MaintenanceCosts = Required("maintenanceCosts", 1m, true),
            AdminCosts = Required("adminCosts", 1m, true),
            BusinessEntertainmentCosts = Required("businessEntertainmentCosts", 1m, true),
            AdvertisingCosts = Required("advertisingCosts", 1m, true),
            InterestOnBankOtherLoans = Required("interestOnBankOtherLoans", 1m, true),
            FinanceCharges = Required("financeCharges", 1m, true),
            IrrecoverableDebts = Required("irrecoverableDebts", 1m, true),
            ProfessionalFees = Required("professionalFees", 1m, true),
            Depreciation = Required("depreciation", 1m, true),
            OtherExpenses = Required("otherExpenses", 1m, true)
        };
    }

    private static int? ValidateTaxYear(string taxYear, TaxReportingPeriod period,
        ICollection<PreparedArtifactFinding> findings)
    {
        var match = TaxYear.Match(taxYear ?? string.Empty);
        if (!match.Success || int.Parse(match.Groups["end"].Value) != (int.Parse(match.Groups["start"].Value) + 1) % 100)
        {
            findings.Add(Error("ITSA-TAX-YEAR-INVALID", "Tax year must use the form YYYY-YY.", "taxYear"));
            return null;
        }
        var year = int.Parse(match.Groups["start"].Value);
        if (year < 2025)
            findings.Add(Error("ITSA-TAX-YEAR-UNSUPPORTED", "Cumulative submissions are supported from tax year 2025-26.", "taxYear"));
        if (period.Start < new DateOnly(year, 4, 6) || period.End > new DateOnly(year + 1, 4, 5))
            findings.Add(Error("ITSA-PERIOD-OUTSIDE-TAX-YEAR", "The cumulative period is outside the requested tax year."));
        return year;
    }

    private static void ValidateCumulativePeriod(SelfEmploymentFilingContext filing, TaxReportingPeriod period,
        int taxYearStart, ICollection<PreparedArtifactFinding> findings)
    {
        if (filing.QuarterlyPeriodType.Equals("CALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Error("ITSA-CALENDAR-OBLIGATION-REQUIRED",
                "Calendar-quarter cumulative dates require an HMRC obligation-backed workflow period."));
            return;
        }
        if (!filing.QuarterlyPeriodType.Equals("STANDARD", StringComparison.OrdinalIgnoreCase)) return;
        var requiredStart = new DateOnly(taxYearStart, 4, 6);
        var allowedEnds = new HashSet<DateOnly>
        {
            new(taxYearStart, 7, 5), new(taxYearStart, 10, 5),
            new(taxYearStart + 1, 1, 5), new(taxYearStart + 1, 4, 5)
        };
        if (period.Start != requiredStart || !allowedEnds.Contains(period.End))
            findings.Add(Error("ITSA-STANDARD-PERIOD-INVALID",
                "A standard cumulative period must start on 6 April and end on an applicable quarterly boundary."));
    }

    private static string NormalizeNino(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static PreparedArtifactFinding Error(string code, string message, string? path = null) =>
        new(PreparedFindingSeverity.Error, code, message, path);
    private static PreparedArtifactFinding Finding(SourceFinding finding) => new(
        finding.Severity switch
        {
            SourceFindingSeverity.Error => PreparedFindingSeverity.Error,
            SourceFindingSeverity.Warning => PreparedFindingSeverity.Warning,
            _ => PreparedFindingSeverity.Information
        }, finding.Code, finding.Message, finding.StableFactKey);
}
