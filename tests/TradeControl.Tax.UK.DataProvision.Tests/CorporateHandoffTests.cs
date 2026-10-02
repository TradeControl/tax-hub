using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis5_9;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026;

internal static class CorporateHandoffTests
{
    public static async Task RunAsync()
    {
        var accounts = FixtureAccounts();
        var accountsPreparer = new CompanyAccountsPreparer();
        var full = accountsPreparer.Prepare(accounts, false, "Example Limited statutory accounts", "01234567-full-accounts.xhtml");
        var filleted = accountsPreparer.Prepare(accounts, true, "Example Limited statutory accounts", "01234567-filleted-accounts.xhtml");

        var companiesHouse = new CompaniesHouseAccountsPreparer().Prepare(
            accounts, filleted, "ENV-HANDOFF-1", new(true, true, true), "TIS-5.9", new(2026, 9, 13), false);
        var computation = new CorporationTaxComputation(accounts.Period, 28000m,
            [new("Depreciation", 5000m)], [new("Non-trading income", 1000m)], new(2000m, 0m, 0m),
            new(0m, 0m, 0m, 0m), 0m, 30000m, 0.19m, 5700m, 0m, 5700m);
        var taxReturn = new Ct600Return("Example Limited", "01234567", "1234567890", accounts.Period,
            120000m, 28000m, 30000m, 5700m, 5700m, true, true, null,
            "Jane Director", new(2026, 7, 31));
        var corporationTax = new CorporationTaxPreparer().Prepare(
            new(computation, taxReturn), full, "CT-HANDOFF-1").Package;

        await AssertUnchangedAsync(companiesHouse, "Companies House");
        await AssertCorporationTaxGateAsync(corporationTax);

        Assert(companiesHouse.ServiceCode == "COMPANIES-HOUSE-ACCOUNTS"
            && companiesHouse.Polling.Mode == SubmissionPollingMode.PollUntilTerminal
            && companiesHouse.Documents.Single().Name == "01234567-filleted-accounts.xhtml",
            "Companies House package composition or polling semantics changed.");
        Assert(corporationTax.ServiceCode == "HMRC-CORPORATION-TAX"
            && corporationTax.Polling.Mode == SubmissionPollingMode.TransactionEngine
            && corporationTax.Polling.RelativeStatusPath is null
            && corporationTax.Documents.Select(x => x.Artifact.OperationCode)
                .SequenceEqual(["STATUTORY-ACCOUNTS", "CORPORATION-TAX-COMPUTATION"]),
            "Corporation Tax package composition or polling semantics changed.");
        Assert(CompanyServiceCoverageCatalog.Services.All(x => !string.IsNullOrWhiteSpace(x.Rationale)),
            "A corporate service has no coverage rationale.");
        foreach (var contract in CompanyContractRegistry.All)
            Assert(CompanyServiceCoverageCatalog.Services.Any(x =>
                    x.ContractId == contract.ContractId && x.ContractVersion == contract.Version),
                $"Contract {contract.ContractId} {contract.Version} is absent from the corporate service matrix.");
    }

    private static async Task AssertCorporationTaxGateAsync(PreparedSubmissionPackage package)
    {
        var transmission = CaptureSnapshot(package.Transmission);
        var documents = package.Documents.Select(document => CaptureSnapshot(document.Artifact)).ToArray();
        var currentArtifacts = package.Documents.Select(document => document.Artifact)
            .Prepend(package.Transmission).ToArray();
        Assert(currentArtifacts.Length == 3
            && currentArtifacts.All(artifact => artifact.Status == PreparedArtifactStatus.Preview),
            "Every current Corporation Tax artifact must remain explicitly preview-only.");

        var context = new AuthorityDispatchContext(
            "tenant-ct", "principal-ct", "actor-ct", "approval-ct", "facts-ct");
        var gateway = new RecordingCorporationTaxGateway();
        await AssertRejectedWithoutSendAsync(gateway,
            new(CorporationTaxSubmissionOperation.SubmitToAuthority, package), context,
            "The current all-preview Corporation Tax package crossed the package gate.");

        var allReady = ClonePackage(package, (_, artifact) => WithStatus(artifact, PreparedArtifactStatus.SubmissionReady));
        var mixed = ClonePackage(package, (index, artifact) =>
            WithStatus(artifact, index == 1 ? PreparedArtifactStatus.Preview : PreparedArtifactStatus.SubmissionReady));
        await AssertRejectedWithoutSendAsync(gateway,
            new(CorporationTaxSubmissionOperation.SubmitToAuthority, mixed), context,
            "A mixed-readiness Corporation Tax package crossed the package gate.");
        await AssertRejectedWithoutSendAsync(gateway,
            new(CorporationTaxSubmissionOperation.Diagnostic, allReady), context,
            "A diagnostic Corporation Tax operation crossed the package gate.");
        var restStylePolling = new PreparedSubmissionPackage(package.ServiceCode, allReady.Transmission,
            allReady.Documents, new(SubmissionPollingMode.TransactionEngine, "status/ct-01"));
        await AssertRejectedWithoutSendAsync(gateway,
            new(CorporationTaxSubmissionOperation.SubmitToAuthority, restStylePolling), context,
            "A Corporation Tax package with an invented REST-style polling path crossed the package gate.");

        var blocked = ClonePackage(package, (index, artifact) => PreparedStatutoryArtifact.Create(
            artifact.JurisdictionCode, artifact.AuthorityCode, artifact.OperationCode, artifact.ContractVersion,
            PreparedArtifactStatus.SubmissionReady, artifact.MediaType, artifact.Content.AsSpan(),
            artifact.SourceEvidence, index == 2
                ? [new(PreparedFindingSeverity.Error, "CT-BLOCKED", "Synthetic blocking finding.")]
                : artifact.Findings));
        await AssertRejectedWithoutSendAsync(gateway,
            new(CorporationTaxSubmissionOperation.SubmitToAuthority, blocked), context,
            "An error-bearing Corporation Tax package crossed the package gate.");

        var unsupported = ClonePackage(package,
            (_, artifact) => WithStatus(artifact, PreparedArtifactStatus.Unsupported));
        await AssertRejectedWithoutSendAsync(gateway,
            new(CorporationTaxSubmissionOperation.SubmitToAuthority, unsupported), context,
            "An unsupported Corporation Tax package crossed the package gate.");
        await AssertRejectedWithoutSendAsync(gateway,
            new(CorporationTaxSubmissionOperation.SubmitToAuthority, allReady), context,
            "Relabelling diagnostic Corporation Tax artifacts made them dispatchable without an approved service identity.");

        Assert(SameSnapshot(CaptureSnapshot(package.Transmission), transmission)
            && package.Documents.Select(document => CaptureSnapshot(document.Artifact)).Zip(documents)
                .All(pair => SameSnapshot(pair.First, pair.Second)),
            "Corporation Tax diagnostic bytes or SHA-256 values changed while exercising the gate.");
    }

    private static PreparedSubmissionPackage ClonePackage(PreparedSubmissionPackage package,
        Func<int, PreparedStatutoryArtifact, PreparedStatutoryArtifact> transform)
    {
        var transmission = transform(0, package.Transmission);
        var documents = package.Documents.Select((document, index) =>
            new PreparedPackageDocument(document.Name, transform(index + 1, document.Artifact)));
        return new(package.ServiceCode, transmission, documents, package.Polling);
    }

    private static PreparedStatutoryArtifact WithStatus(PreparedStatutoryArtifact artifact,
        PreparedArtifactStatus status) => PreparedStatutoryArtifact.Create(
        artifact.JurisdictionCode, artifact.AuthorityCode, artifact.OperationCode, artifact.ContractVersion,
        status, artifact.MediaType, artifact.Content.AsSpan(), artifact.SourceEvidence, artifact.Findings);

    private static Snapshot CaptureSnapshot(PreparedStatutoryArtifact artifact) => new(
        string.Empty, artifact.MediaType, artifact.Sha256, artifact.Content.ToArray());

    private static bool SameSnapshot(Snapshot left, Snapshot right) =>
        left.MediaType == right.MediaType && left.Sha256 == right.Sha256
        && left.Content.SequenceEqual(right.Content);

    private static async Task AssertRejectedWithoutSendAsync(RecordingCorporationTaxGateway gateway,
        CorporationTaxSubmissionCommand command, AuthorityDispatchContext context, string message)
    {
        try
        {
            await gateway.SendAsync(command, context);
        }
        catch (InvalidOperationException)
        {
            Assert(gateway.SendCount == 0, $"{message} Outbound send count was {gateway.SendCount}.");
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task AssertUnchangedAsync(PreparedSubmissionPackage package, string name)
    {
        var gateway = new CapturingGateway();
        var transmission = package.Transmission.Content.ToArray();
        var documents = package.Documents.Select(x => new Snapshot(
            x.Name, x.Artifact.MediaType, x.Artifact.Sha256, x.Artifact.Content.ToArray())).ToArray();
        await gateway.SendAsync(package);

        Assert(ReferenceEquals(package, gateway.Received), $"{name} gateway reconstructed the package.");
        Assert(package.Transmission.Content.AsSpan().SequenceEqual(transmission), $"{name} transmission bytes changed at handoff.");
        Assert(package.Documents.Select(x => new Snapshot(x.Name, x.Artifact.MediaType, x.Artifact.Sha256, x.Artifact.Content.ToArray()))
            .Zip(documents).All(pair => pair.First.Name == pair.Second.Name
                && pair.First.MediaType == pair.Second.MediaType
                && pair.First.Sha256 == pair.Second.Sha256
                && pair.First.Content.SequenceEqual(pair.Second.Content)),
            $"{name} constituent documents changed at handoff.");
    }

    private sealed class CapturingGateway : ICompaniesHouseSubmissionPackageGateway
    {
        public PreparedSubmissionPackage? Received { get; private set; }
        public Task SendAsync(PreparedSubmissionPackage package, CancellationToken cancellationToken = default)
        {
            Received = package;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCorporationTaxGateway : CorporationTaxSubmissionGateway
    {
        public int SendCount { get; private set; }

        protected override Task<CorporationTaxSubmissionOutcome> SendEligibleAsync(
            CorporationTaxSubmissionCommand command, AuthorityDispatchContext context,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(CorporationTaxSubmissionOutcome.RecoveryRequired());
        }
    }

    private sealed record Snapshot(string Name, string MediaType, string Sha256, byte[] Content);

    private static StatutoryAccounts FixtureAccounts()
    {
        ComparativeAmount A(decimal current, decimal comparative) => new(current, comparative);
        return new(
            new("Example Limited", "01234567"),
            new(new(2025, 7, 1), new(2026, 6, 30)),
            new(new(2024, 7, 1), new(2025, 6, 30)),
            new(ReportingFramework.Frs105, AccountsType.MicroEntity, AuditStatus.UnauditedExempt, true, true),
            new(A(12000, 10000), A(35000, 30000), A(1000, 800), A(18000, 16000), A(18000, 14800),
                A(30000, 24800), A(5000, 4000), A(0, 0), A(0, 0), A(25000, 20800), A(25000, 20800)),
            new(A(120000, 100000), A(1000, 500), A(60000, 50000), A(33000, 29000), A(5700, 4085), A(22300, 17415)),
            new("Software development", "FRS 105 historical cost basis", 3, [], []),
            new(new(2026, 7, 31), "Jane Director"));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
