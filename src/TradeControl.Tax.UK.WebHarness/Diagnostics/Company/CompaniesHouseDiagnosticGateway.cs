using TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Tools.CompaniesHouseSimulator;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public interface ICompaniesHouseDiagnosticGatewayFactory
{
    ICompaniesHouseXmlGatewaySession Create(CompaniesHouseAccountsPayload payload);
}

public sealed class InProcessCompaniesHouseDiagnosticGatewayFactory : ICompaniesHouseDiagnosticGatewayFactory
{
    private static readonly SimulatorAuthentication Authentication = new(
        "SIM-WEB-HARNESS", "SIM-WEB-HARNESS-AUTH", "SIM-WEB-HARNESS-COMPANY");

    public ICompaniesHouseXmlGatewaySession Create(CompaniesHouseAccountsPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payload.Simulation);
        if (!SimulatorScenarioCatalog.All.TryGetValue(payload.Simulation.Scenario, out var scenario))
            throw new ArgumentException("The selected Companies House simulator scenario is unsupported.", nameof(payload));
        var simulatedNow = payload.AsOfDate is { } asOfDate
            ? new DateTimeOffset(asOfDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        return new InProcessCompaniesHouseDiagnosticGateway(
            new CompaniesHouseDevelopmentSimulator(new FixedSimulatorClock(simulatedNow),
                new SequentialSimulatorReferenceGenerator("SIM-WEB-HARNESS")),
            Authentication, scenario);
    }
}

public sealed class SendDisabledCompaniesHouseOfficialTestGatewayFactory : ICompaniesHouseDiagnosticGatewayFactory
{
    public ICompaniesHouseXmlGatewaySession Create(CompaniesHouseAccountsPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new SendDisabledCompaniesHouseOfficialTestGateway();
    }
}

internal sealed class InProcessCompaniesHouseDiagnosticGateway(
    CompaniesHouseDevelopmentSimulator simulator,
    SimulatorAuthentication authentication,
    SimulatorScenario scenario) : ICompaniesHouseXmlGatewaySession
{
    public string EvidenceClass => SimulatorResponse.SimulatedEvidence;

    public Task<CompaniesHouseGatewayResponse> ExchangeAsync(
        CompaniesHouseGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var response = simulator.Process(request.Content.AsSpan(), authentication,
            request.Operation == CompaniesHouseGatewayOperation.FormSubmission ? scenario : null);
        return Task.FromResult(new CompaniesHouseGatewayResponse(
            response.Kind switch
            {
                SimulatorResponseKind.Acknowledgement => CompaniesHouseGatewayResponseKind.Acknowledgement,
                SimulatorResponseKind.SubmissionStatus => CompaniesHouseGatewayResponseKind.SubmissionStatus,
                SimulatorResponseKind.StatusAcknowledgement => CompaniesHouseGatewayResponseKind.StatusAcknowledgement,
                _ => CompaniesHouseGatewayResponseKind.GatewayError
            },
            response.Content,
            response.RequestSha256,
            response.ResponseSha256,
            response.EvidenceClass));
    }
}
