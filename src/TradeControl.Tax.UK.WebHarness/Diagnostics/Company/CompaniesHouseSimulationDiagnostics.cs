using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;
using TradeControl.Tax.UK.Tools.CompaniesHouseSimulator;
using System.Text;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public interface ICompaniesHouseSimulationDiagnostics
{
    bool Enabled { get; }
    Task<CompaniesHouseSimulationOutcome> ExecuteAsync(CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed record CompaniesHouseSimulationSummary(string EvidenceClass, string Scenario,
    string SubmissionNumber, string FinalState, bool IsTerminal, string TransmissionSha256,
    string AccountsSha256, string InitialRequestSha256, string InitialResponseSha256,
    IReadOnlyList<string> States, IReadOnlyList<CompaniesHouseError> Errors,
    bool StatusAcknowledgementRequired, bool StatusAcknowledged);

public sealed record CompaniesHouseProtocolExchange(string Operation, string RequestXml, string ResponseXml);

public sealed record CompaniesHouseProtocolTranscript(string EvidenceClass,
    IReadOnlyList<CompaniesHouseProtocolExchange> Exchanges);

public sealed record CompaniesHouseSimulationOutcome(CompaniesHouseSimulationSummary TaxHub,
    CompaniesHouseProtocolTranscript CompaniesHouse);

public sealed class CompaniesHouseSimulationDiagnostics(
    ICompaniesHouseAccountsRunner accountsRunner,
    ICompaniesHouseDiagnosticGatewayFactory gatewayFactory) : ICompaniesHouseSimulationDiagnostics
{
    private static readonly HashSet<string> ExpectedPreparationBlockers = new(StringComparer.Ordinal)
    {
        "CH-PENDING-CREDENTIALS",
        "CH-PENDING-DEVELOPER-TEST"
    };

    public bool Enabled => true;

    public async Task<CompaniesHouseSimulationOutcome> ExecuteAsync(CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payload.Simulation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SimulatorScenarioCatalog.All.TryGetValue(payload.Simulation.Scenario, out var scenario))
            throw new ArgumentException("The selected Companies House simulator scenario is unsupported.", nameof(payload));

        var package = await accountsRunner.PrepareAsync(payload, cancellationToken);
        var unexpectedErrors = package.Transmission.Findings
            .Concat(package.Documents.SelectMany(document => document.Artifact.Findings))
            .Where(finding => finding.Severity == PreparedFindingSeverity.Error
                && !ExpectedPreparationBlockers.Contains(finding.Code))
            .ToArray();
        if (unexpectedErrors.Length != 0)
            throw new InvalidOperationException(
                $"The Companies House preparation has blocking findings: {string.Join(", ", unexpectedErrors.Select(x => x.Code))}.");

        var accounts = package.Documents.Single().Artifact;
        var gateway = gatewayFactory.Create(payload);
        var acknowledgement = await gateway.ExchangeAsync(new(
            CompaniesHouseGatewayOperation.FormSubmission, package.Transmission.Content), cancellationToken);
        if (acknowledgement.Kind != CompaniesHouseGatewayResponseKind.Acknowledgement)
            throw new InvalidOperationException("The selected Companies House gateway rejected the prepared filing envelope.");
        var exchanges = new List<CompaniesHouseProtocolExchange>
        {
            Exchange("FormSubmission", package.Transmission.Content.AsSpan(), acknowledgement.Content.AsSpan())
        };

        var conversation = CompaniesHouseFilingConversation.Start(payload.Filing.SubmissionNumber,
                package.Transmission.Sha256, accounts.Sha256)
            .ApplyAcknowledgement(new(null, null, null, []),
                Evidence(acknowledgement, $"simulator/{payload.Filing.SubmissionNumber}/acknowledgement"));
        var states = new List<string> { conversation.State.ToString() };
        var serializer = new CompaniesHouseEnvelopeSerializer();
        var pollRequest = serializer.SerializeStatusRequest(new("SIM-WEB-HARNESS",
            CompaniesHouseStatusQueryScope.Submission, payload.Filing.SubmissionNumber));
        var maximumPolls = scenario.Statuses.Length + 1;
        for (var poll = 1; poll <= maximumPolls && !conversation.IsTerminal; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await gateway.ExchangeAsync(new(
                CompaniesHouseGatewayOperation.GetSubmissionStatus,
                System.Collections.Immutable.ImmutableArray.CreateRange(pollRequest.Content)), cancellationToken);
            if (response.Kind != CompaniesHouseGatewayResponseKind.SubmissionStatus)
                throw new InvalidOperationException("The selected Companies House gateway did not return a submission status.");
            exchanges.Add(Exchange("GetSubmissionStatus", pollRequest.Content.AsSpan(), response.Content.AsSpan()));
            var status = serializer.ParseSubmissionStatuses(response.Content.AsSpan()).SingleOrDefault()
                ?? throw new InvalidOperationException("The simulator returned no matching submission status.");
            conversation = conversation.ApplyStatus(status, false,
                Evidence(response, $"simulator/{payload.Filing.SubmissionNumber}/status-{poll}"));
            states.Add(conversation.State.ToString());
        }
        if (!conversation.IsTerminal)
            throw new InvalidOperationException("The bounded simulator conversation did not reach a terminal result.");

        var summary = new CompaniesHouseSimulationSummary(gateway.EvidenceClass,
            scenario.Name, conversation.SubmissionNumber, conversation.State.ToString(), conversation.IsTerminal,
            conversation.TransmissionSha256, conversation.AccountsSha256, acknowledgement.RequestSha256,
            acknowledgement.ResponseSha256, states, conversation.Errors,
            conversation.StatusAcknowledgementRequired, conversation.StatusAcknowledged);
        return new(summary, new(gateway.EvidenceClass, exchanges));
    }

    private static CompaniesHouseSimulationEvidence Evidence(CompaniesHouseGatewayResponse response, string reference) =>
        new(reference, response.RequestSha256, response.ResponseSha256);

    private static CompaniesHouseProtocolExchange Exchange(string operation, ReadOnlySpan<byte> request,
        ReadOnlySpan<byte> response) => new(operation, Encoding.UTF8.GetString(request),
            Encoding.UTF8.GetString(response));
}

public sealed class DisabledCompaniesHouseSimulationDiagnostics : ICompaniesHouseSimulationDiagnostics
{
    public bool Enabled => false;

    public Task<CompaniesHouseSimulationOutcome> ExecuteAsync(CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Companies House simulation is available only in Development.");
}
