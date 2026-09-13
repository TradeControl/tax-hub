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
        await AssertUnchangedAsync(corporationTax, "Corporation Tax");

        Assert(companiesHouse.ServiceCode == "COMPANIES-HOUSE-ACCOUNTS"
            && companiesHouse.Polling.Mode == SubmissionPollingMode.PollUntilTerminal
            && companiesHouse.Documents.Single().Name == "01234567-filleted-accounts.xhtml",
            "Companies House package composition or polling semantics changed.");
        Assert(corporationTax.ServiceCode == "HMRC-CORPORATION-TAX"
            && corporationTax.Polling.Mode == SubmissionPollingMode.None
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

    private sealed class CapturingGateway : IPreparedSubmissionPackageGateway
    {
        public PreparedSubmissionPackage? Received { get; private set; }
        public Task SendAsync(PreparedSubmissionPackage package, CancellationToken cancellationToken = default)
        {
            Received = package;
            return Task.CompletedTask;
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
