using System.Security.Cryptography;
using System.Text;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;

internal static class PreparedArtifactTests
{
    public static void Run()
    {
        var original = Encoding.UTF8.GetBytes("{\"period\":\"2026\"}");
        var artifact = PreparedStatutoryArtifact.Create(
            "GB", "HMRC", "VAT-RETURN", "v1.0", PreparedArtifactStatus.Preview,
            "application/json", original,
            [new SourceVersion("Cash.fnTaxVatSummary", "0000000000000001", null)]);

        original[0] = 0;
        Assert(Encoding.UTF8.GetString(artifact.Content.AsSpan()) == "{\"period\":\"2026\"}",
            "Prepared bytes retained a mutable caller-owned buffer.");
        Assert(artifact.Sha256 == Convert.ToHexString(SHA256.HashData(artifact.Content.AsSpan())),
            "The digest was not calculated over the final stored bytes.");

        var request = new PreparedApiRequest(
            artifact, "post", "/organisations/vat/returns",
            [new("periodKey", "24A1"), new("finalised", "true")],
            [new("Accept", "application/json"), new("Content-Type", "application/json")]);
        Assert(request.Method == "POST" && request.Query[0].Name == "periodKey"
            && request.Query[1].Name == "finalised",
            "Prepared request ordering was not retained.");

        AssertRejected(() => new PreparedApiRequest(
            artifact, "POST", "https://example.test/returns"),
            "A prepared request accepted a base address.");
        AssertRejected(() => new PreparedApiRequest(
            artifact, "POST", "/returns", headers: [new("Authorization", "Bearer secret")]),
            "A prepared request accepted credentials.");

        var document = PreparedStatutoryArtifact.Create(
            "GB", "COMPANIES-HOUSE", "STATUTORY-ACCOUNTS", "TIS5.9",
            PreparedArtifactStatus.SubmissionReady, "application/xhtml+xml",
            Encoding.UTF8.GetBytes("<html>accounts</html>"));
        var envelope = PreparedStatutoryArtifact.Create(
            "GB", "COMPANIES-HOUSE", "FILE-ACCOUNTS", "TIS5.9",
            PreparedArtifactStatus.SubmissionReady, "application/xml",
            Encoding.UTF8.GetBytes("<envelope>encoded-document</envelope>"));
        var package = new PreparedSubmissionPackage(
            "COMPANIES-HOUSE-XML-GATEWAY", envelope,
            [new("accounts.xhtml", document)],
            new(SubmissionPollingMode.PollUntilTerminal, "/submissions/{submissionId}"));

        Assert(!package.Transmission.Content.AsSpan().SequenceEqual(package.Documents[0].Artifact.Content.AsSpan()),
            "The transmitted envelope was conflated with its inspectable document.");
    }

    private static void AssertRejected(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
