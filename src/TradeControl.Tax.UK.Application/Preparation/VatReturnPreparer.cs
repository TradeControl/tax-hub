using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Hmrc.Vat;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed record PrepareVatReturn(
    SourceKey Source,
    TaxReportingPeriod Period,
    string PeriodKey,
    bool Finalised,
    string? VatRegistrationOverride = null);

public sealed class VatReturnPreparer
{
    private readonly IVatReturnSourceReader _source;
    private readonly ISourceReadinessEvaluator _readiness;
    private readonly ISourceStatutoryContextReader _context;
    private readonly PreparedApiRequestPipeline _pipeline;

    public VatReturnPreparer(IVatReturnSourceReader source, ISourceReadinessEvaluator readiness,
        ISourceStatutoryContextReader context, PreparedApiRequestPipeline pipeline)
    {
        _source = source;
        _readiness = readiness;
        _context = context;
        _pipeline = pipeline;
    }

    public async Task<PreparedApiRequest> PrepareAsync(
        PrepareVatReturn command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var findings = new List<PreparedArtifactFinding>();
        StatutoryContextSnapshot? context = null;
        VatReturnSource? source = null;
        try { context = await _context.ReadAsync(command.Source, command.Period.End, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            findings.Add(Error("VAT-CONTEXT-UNAVAILABLE", exception.Message));
        }
        try { source = await _source.ReadAsync(new(command.Source, command.Period), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            findings.Add(Error("VAT-PERIOD-UNAVAILABLE", exception.Message));
        }
        if (source is not null)
        {
            try
            {
                var readiness = await _readiness.EvaluateAsync(
                    new(command.Source, source.Subject, source.Period), cancellationToken);
                findings.AddRange(readiness.Findings.Select(Finding));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                findings.Add(Error("VAT-READINESS-UNAVAILABLE", exception.Message));
            }
        }

        var vrn = Digits(string.IsNullOrWhiteSpace(command.VatRegistrationOverride)
            ? context?.Identity.VatNumber : command.VatRegistrationOverride);
        if (vrn.Length != 9)
            findings.Add(Error("VAT-REGISTRATION-INVALID", "An effective nine-digit VAT registration is required.", "vrn"));
        if (string.IsNullOrWhiteSpace(command.PeriodKey))
            findings.Add(Error("VAT-PERIOD-KEY-MISSING", "The HMRC obligation period key is required.", "periodKey"));
        if (!command.Finalised)
            findings.Add(Error("VAT-DECLARATION-NOT-FINALISED", "The VAT return declaration must be finalised.", "finalised"));

        VatReturnRequest? body = null;
        try
        {
            if (source is not null)
                body = Populate(source, vrn, command.PeriodKey, command.Finalised);
        }
        catch (InvalidOperationException exception) { findings.Add(Error("VAT-SOURCE-INVALID", exception.Message)); }

        var descriptor = VatOperationCatalog.All.Single(item => item.OperationId == "vat.returns.submit");
        return _pipeline.Prepare(HmrcPreparedApiContracts.From(descriptor),
            [new("vrn", vrn.Length == 0 ? "invalid" : vrn)],
            serializeBody: () => VatJson.SerializeCanonical(body!),
            sourceEvidence: source is null ? [] :
                [new(source.Provenance.SourceSystem, source.Provenance.DatasetKey, source.Provenance.SnapshotToken)],
            validationStages: [new("vat-source-and-readiness", () => findings)]);
    }

    public static VatReturnRequest Populate(VatReturnSource source, string vrn, string periodKey, bool finalised)
    {
        decimal Amount(TaxFact<decimal> fact) => fact.Value.HasValue
            ? fact.Value.Value : throw new InvalidOperationException($"VAT fact '{fact.StableKey}' has no usable value.");
        static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        static decimal Whole(decimal value) => decimal.Round(value, 0, MidpointRounding.AwayFromZero);

        var box1 = Money(Amount(source.VatDueSales));
        var box2 = Money(Math.Abs(Amount(source.VatDueAcquisitions)) + Amount(source.VatAdjustment));
        if (box2 < 0m) throw new InvalidOperationException("VAT adjustment makes box 2 negative.");
        var box3 = Money(box1 + box2);
        var box4 = Money(Math.Abs(Amount(source.VatReclaimedCurrentPeriod)));
        var box5 = Money(Math.Abs(box3 - box4));
        return new()
        {
            Vrn = vrn,
            PeriodKey = periodKey.Trim(),
            VatDueSales = box1,
            VatDueAcquisitions = box2,
            TotalVatDue = box3,
            VatReclaimedCurrPeriod = box4,
            NetVatDue = box5,
            TotalValueSalesExVat = Whole(Amount(source.TotalValueSalesExVat)),
            TotalValuePurchasesExVat = Whole(Amount(source.TotalValuePurchasesExVat)),
            TotalValueGoodsSuppliedExVat = Whole(Amount(source.TotalValueGoodsSuppliedExVat)),
            TotalAcquisitionsExVat = Whole(Amount(source.TotalAcquisitionsExVat)),
            Finalised = finalised
        };
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
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
