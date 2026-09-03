using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Finalisation.V8;

public static class FinalisationEndpoints
{
    private const string Root = "/individuals/calculations/{nino}/self-assessment/{taxYear}/{calculationId}";
    private const string Accept = "application/vnd.hmrc.8.0+json";
    private static readonly EndpointParameter[] Parameters = [new("nino"), new("taxYear"), new("calculationId")];
    public static readonly HmrcEndpoint FinalDeclaration = new("Submit final declaration", "POST", Root + "/final-declaration", "8.0", Accept, "write:self-assessment", 204, Parameters, [], false);
    public static readonly HmrcEndpoint ConfirmAmendment = new("Confirm amendment", "POST", Root + "/confirm-amendment", "8.0", Accept, "write:self-assessment", 204, Parameters, [], false);
    public static IReadOnlyList<HmrcEndpoint> All => [FinalDeclaration, ConfirmAmendment];
}
